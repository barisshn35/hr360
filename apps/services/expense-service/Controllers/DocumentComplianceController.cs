using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;
using ExpenseService.Tenancy;

namespace ExpenseService.Controllers;

/// <summary>
/// Eksik belge takibi (yenilik haritası #11): İK zorunlu belge türlerini tanımlar; çalışan dosyalarında
/// eksik, süresi dolmuş ve dolmak üzere olan belgeler listelenir, çalışana hatırlatma gider. Çalışan
/// yalnızca kendi eksiklerini görür (Yapılacaklarım).
/// </summary>
[ApiController]
[Route("api/documents/compliance")]
[Authorize]
public class DocumentComplianceController : ControllerBase
{
    private readonly ExpenseDbContext _db;
    private readonly ApprovalWorkflowClient _employees;
    private readonly ITenantContext _tenant;
    public DocumentComplianceController(ExpenseDbContext db, ApprovalWorkflowClient employees, ITenantContext tenant)
    {
        _db = db; _employees = employees; _tenant = tenant;
    }

    public sealed class EmpRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Guid? DepartmentId { get; set; }
        public string? DepartmentName { get; set; }
    }

    private Task<List<EmpRow>> EmployeesAsync(CancellationToken ct) => _db.Database.SqlQueryRaw<EmpRow>(
        """
        SELECT e."Id", e."FirstName" || ' ' || e."LastName" AS "Name", a."DepartmentId", d."Name" AS "DepartmentName"
        FROM employee_employees e
        LEFT JOIN LATERAL (SELECT x."DepartmentId" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= current_date AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        WHERE e."TenantSlug" = {0} AND e."Status" <> 'Terminated'
        ORDER BY 2
        """, _tenant.TenantSlug ?? "").ToListAsync(ct);

    private static object IssueView(DocumentCompliance.Issue i) => new { type = i.Type.ToString(), state = i.State, validUntil = i.ValidUntil };

    [HttpGet("requirements")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> GetRequirements(CancellationToken ct) =>
        Ok((await _db.DocumentRequirements.AsNoTracking().ToListAsync(ct))
            .Select(r => new { r.Id, type = r.Type.ToString(), r.DepartmentId, r.ValidityMonths }));

    public sealed record RequirementInput(DocumentType Type, Guid? DepartmentId, int? ValidityMonths);

    /// <summary>Gereksinim listesini bütünüyle değiştirir.</summary>
    [HttpPut("requirements")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> PutRequirements([FromBody] List<RequirementInput> body, CancellationToken ct)
    {
        if (body.Count > 100) return BadRequest(new { message = "En fazla 100 gereksinim tanımlanabilir" });
        if (body.Any(r => r.ValidityMonths is < 1 or > 120)) return BadRequest(new { message = "Geçerlilik 1-120 ay olmalı" });
        if (body.GroupBy(r => (r.Type, r.DepartmentId)).Any(g => g.Count() > 1)) return BadRequest(new { message = "Aynı belge aynı kapsamda iki kez tanımlanmış" });
        _db.DocumentRequirements.RemoveRange(await _db.DocumentRequirements.ToListAsync(ct));
        _db.DocumentRequirements.AddRange(body.Select(r => new DocumentRequirement { Type = r.Type, DepartmentId = r.DepartmentId, ValidityMonths = r.ValidityMonths }));
        await _db.SaveChangesAsync(ct);
        return await GetRequirements(ct);
    }

    /// <summary>İK: eksiği olan çalışanlar ve özet. <paramref name="departmentId"/> ile süzülür.</summary>
    [HttpGet]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> Overview([FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var reqs = await _db.DocumentRequirements.AsNoTracking().ToListAsync(ct);
        var people = (await EmployeesAsync(ct)).Where(p => departmentId is null || p.DepartmentId == departmentId).ToList();
        var ids = people.Select(p => p.Id).ToList();
        var docs = (await _db.Documents.AsNoTracking().Where(d => ids.Contains(d.EmployeeId)).ToListAsync(ct)).ToLookup(d => d.EmployeeId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = people.Select(p => (p, issues: DocumentCompliance.Evaluate(reqs, p.DepartmentId, docs[p.Id], today))).ToList();
        return Ok(new
        {
            requirementCount = reqs.Count,
            summary = new
            {
                employees = rows.Count,
                compliant = rows.Count(r => r.issues.Count == 0),
                missing = rows.Sum(r => r.issues.Count(i => i.State == "Missing")),
                expired = rows.Sum(r => r.issues.Count(i => i.State == "Expired")),
                expiringSoon = rows.Sum(r => r.issues.Count(i => i.State == "ExpiringSoon")),
            },
            rows = rows.Where(r => r.issues.Count > 0).Select(r => new
            {
                employeeId = r.p.Id, name = r.p.Name, department = r.p.DepartmentName, issues = r.issues.Select(IssueView),
            }),
        });
    }

    /// <summary>Çalışan: kendi eksik ya da süresi dolan belgeleri.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var person = (await EmployeesAsync(ct)).FirstOrDefault(p => p.Id == me);
        var reqs = await _db.DocumentRequirements.AsNoTracking().ToListAsync(ct);
        var docs = await _db.Documents.AsNoTracking().Where(d => d.EmployeeId == me).ToListAsync(ct);
        return Ok(DocumentCompliance.Evaluate(reqs, person?.DepartmentId, docs, DateOnly.FromDateTime(DateTime.UtcNow)).Select(IssueView));
    }

    public sealed record RemindInput(List<Guid> EmployeeIds);

    /// <summary>İK: seçilen çalışanlara eksik belgeleri için uygulama içi hatırlatma.</summary>
    [HttpPost("remind")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> Remind([FromBody] RemindInput body, CancellationToken ct)
    {
        if (body.EmployeeIds is not { Count: > 0 and <= 500 }) return BadRequest(new { message = "1-500 çalışan seçin" });
        var reqs = await _db.DocumentRequirements.AsNoTracking().ToListAsync(ct);
        var people = (await EmployeesAsync(ct)).Where(p => body.EmployeeIds.Contains(p.Id)).ToList();
        var ids = people.Select(p => p.Id).ToList();
        var docs = (await _db.Documents.AsNoTracking().Where(d => ids.Contains(d.EmployeeId)).ToListAsync(ct)).ToLookup(d => d.EmployeeId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sent = 0;
        foreach (var p in people)
        {
            var issues = DocumentCompliance.Evaluate(reqs, p.DepartmentId, docs[p.Id], today);
            if (issues.Count == 0) continue;
            var list = string.Join(", ", issues.Select(i => $"{Label(i.Type)} ({(i.State == "Missing" ? "eksik" : i.State == "Expired" ? "süresi doldu" : "süresi doluyor")})"));
            await _db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ({0},{1},{2},NULL,'InApp','documents.missing',{3},{4},'Pending',0,now())
                """, [Guid.NewGuid(), _tenant.TenantSlug ?? "", p.Id, "Özlük dosyanızda eksik belge var", $"Lütfen İK'ya iletin: {list}."], ct);
            sent++;
        }
        return Ok(new { sent });
    }

    private static string Label(DocumentType t) => t switch
    {
        DocumentType.Contract => "İş sözleşmesi",
        DocumentType.IdCard => "Kimlik",
        DocumentType.Diploma => "Diploma",
        DocumentType.Certificate => "Sertifika",
        DocumentType.Health => "Sağlık raporu",
        DocumentType.Payslip => "Bordro",
        _ => "Diğer belge",
    };
}
