using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CompensationService.Data;
using CompensationService.Models;
using CompensationService.Payroll;
using CompensationService.Tenancy;

namespace CompensationService.Controllers;

/// <summary>
/// Bordro ekosistemi: SGK/banka/muhasebe dosyaları (Y1, Y3, Y4), avans ve borç (Y11),
/// esnek yan haklar (Y13), zam dönemi (Y21).
/// </summary>
[ApiController]
[Route("api/compensation")]
[Authorize]
public class PayrollEcosystemController : ControllerBase
{
    private readonly CompensationDbContext _db;
    private readonly ITenantContext _tenant;
    public PayrollEcosystemController(CompensationDbContext db, ITenantContext tenant) { _db = db; _tenant = tenant; }

    private bool IsPayrollAdmin => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");
    private bool IsManager => IsPayrollAdmin || User.IsInRole("manager");
    private string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value ?? "unknown";
    private string UserName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? UserId;
    private string Tenant => _tenant.TenantSlug ?? "";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // tenant: kaydın kiracısı. Platform yöneticisinin oturumunda kiracı olmadığından kayıt kendi kiracısına yazılır
    // (aksi halde şirketin erişim kayıtlarında görünmüyordu).
    private async Task AuditAsync(string entityType, string entityId, string action, object changes, string? tenant = null)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'compensation-service',{1},{2},{3},{4}::jsonb,{5},{6},{7},{8},now())",
                (object?)(tenant ?? _tenant.TenantSlug) ?? DBNull.Value, entityType, entityId, action, JsonSerializer.Serialize(changes, Json),
                UserId, UserName, (object?)(Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier) ?? DBNull.Value,
                (object?)Request.Headers["X-Real-IP"].FirstOrDefault() ?? DBNull.Value);
        }
        catch (Exception ex)
        {
            // Denetim yazılamazsa iş akışı bozulmaz; ama sessizce yutulmaz (KVKK erişim kaydı eksik kalır).
            HttpContext.RequestServices.GetService<ILogger<PayrollAudit>>()?.LogWarning(ex, "audit_log yazılamadı: {EntityType} {Action}", entityType, action);
        }
    }

    private async Task<Guid?> MyEmployeeIdAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Tenant)) return null;
        return await _db.Database.SqlQueryRaw<Guid?>(
            "SELECT \"Id\" AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"KeycloakUserId\" = {1} LIMIT 1",
            Tenant, UserId).FirstOrDefaultAsync(ct);
    }

    private async Task Notify(Guid employeeId, string subject, string body, string code, CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ({0},{1},{2},NULL,'InApp',{3},{4},{5},'Pending',0,now())
                """, Guid.NewGuid(), Tenant, employeeId, code, subject, body);
        }
        catch (Exception) { }
    }

    private Task<List<PayrollData.PersonRow>> PeopleRowsAsync(CancellationToken ct) => PayrollData.PeopleAsync(_db, Tenant, ct);

    private static ExportPerson ToExport(PayrollData.PersonRow r) => PayrollData.ToExport(r);

    /* ============================================================ Y1/Y3/Y4 dosyalar */

    public record ExportInput(string Kind, string? Format, AccountMap? Accounts, DateOnly? PayDate = null);

    /// <summary>
    /// Kapanmış dönem için dosya üretir. SGK ve banka dosyaları TCKN/IBAN içerir: içerik şifreli
    /// saklanır, banka dosyası bir kez indirilir, hepsi 24 saat sonra silinir. Muhasebe dosyası
    /// yalnızca masraf merkezi toplamlarını içerir. Dalga 8: SGK APHB XML/TXT (format), banka şablonu
    /// (format: generic | ornek-a | ornek-b | custom; boşsa şirket ayarı), şirketin hesap planı ve masraf
    /// merkezi eşlemesi; dengesiz muhasebe fişi üretilmez. Dosyanın SHA-256 özeti kayda ve denetime yazılır.
    /// Dış sistemlere (SGK e-Bildirge, banka, muhasebe yazılımı) bağlanılmaz; yalnızca dosya üretilir.
    /// </summary>
    [HttpPost("payroll/periods/{id:guid}/exports")]
    public async Task<IActionResult> CreateExport(Guid id, [FromBody] ExportInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        if (!ExportCrypto.Enabled) return StatusCode(503, new { message = "TENANT_SECRET_KEY tanımlı değil; dosya şifrelenemediği için üretilmez." });
        var period = await _db.PayrollPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (period is null) return NotFound();
        if (period.Status != PayrollPeriodStatus.Closed)
            return BadRequest(new { message = "Dosyalar yalnızca kapatılmış dönem için üretilir", code = "period_not_closed" });
        var slips = await _db.Payslips.AsNoTracking().Where(s => s.PeriodId == id).ToListAsync(ct);
        var rows = await PeopleRowsAsync(ct);
        var people = rows.ToDictionary(r => r.Id, ToExport);
        var title = (await _db.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM platform_tenants WHERE \"Slug\" = {0}", Tenant).FirstOrDefaultAsync(ct)) ?? Tenant;
        var settings = await PayrollData.SettingsAsync(_db, ct);
        string format;
        ExportResult r;
        switch (body.Kind)
        {
            case "SgkAphb":
            {
                format = (body.Format ?? "xml").ToLowerInvariant();
                if (format is not ("xml" or "txt")) return BadRequest(new { message = "Geçersiz biçim" });
                var (aphbPeople, leave, _, prm) = await PayrollData.AphbInputAsync(_db, Tenant, period, ct);
                if (prm is null) return Conflict(PayrollParameterResolver.Missing(period.Year, period.Month));
                var (aphbRows, issues) = Aphb.Build(period, slips, aphbPeople, leave, settings.Sgk, prm);
                var content = format == "xml" ? Aphb.Xml(period, aphbRows, aphbPeople, title, settings.Sgk) : Aphb.Txt(aphbRows, aphbPeople);
                r = new ExportResult(content, $"sgk-aphb-{period.Year}-{period.Month:00}.{format}", format == "xml" ? "application/xml" : "text/plain",
                    aphbRows.Count, issues.Select(i => $"{i.Name}: {i.Message}").ToList());
                break;
            }
            case "SgkHires": format = "csv"; r = Exporters.SgkHires(period, people.Values); break;
            case "Bank":
                format = (body.Format ?? settings.Bank.Template ?? "generic").ToLowerInvariant();
                if (format is not ("generic" or "ornek-a" or "ornek-b" or "custom")) return BadRequest(new { message = "Geçersiz banka şablonu" });
                r = BankFiles.Build(period, slips, people, settings.Bank with { Template = format },
                    body.PayDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)));
                break;
            case "Accounting":
            {
                format = (body.Format ?? "generic").ToLowerInvariant();
                if (format is not ("generic" or "logo" or "mikro" or "netsis")) return BadRequest(new { message = "Geçersiz biçim" });
                var map = body.Accounts ?? settings.Accounts;
                // Madde 64: borç = alacak denetimi; dengesiz fiş dosyaya dönüşmez.
                var journal = Exporters.Journal(period, slips, people, map, settings.CostCenters);
                if (!Exporters.JournalBalanced(journal))
                    return UnprocessableEntity(new { message = $"Muhasebe fişi dengesiz (borç-alacak farkı {Exporters.JournalDifference(journal):0.00}); dosya üretilmedi.", code = "journal_unbalanced" });
                r = Exporters.Accounting(period, slips, people, format, map, settings.CostCenters);
                break;
            }
            default: return BadRequest(new { message = "Geçersiz dosya türü" });
        }
        var sha = EPayslip.Sha256Bytes(r.Content);
        var e = new PayrollExport
        {
            PeriodId = id, Kind = body.Kind, FileName = r.FileName, ContentType = r.ContentType, Cipher = ExportCrypto.Encrypt(r.Content),
            RowCount = r.Rows, SingleUse = body.Kind == "Bank", CreatedBy = UserName, ContentSha256 = sha, Format = format,
        };
        _db.PayrollExports.Add(e);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("PayrollExport", e.Id.ToString(), "Created", new { e.Kind, e.RowCount, period.Year, period.Month, e.Format, sha256 = sha }, e.TenantSlug);
        return Ok(new { e.Id, e.Kind, e.FileName, e.RowCount, e.SingleUse, e.ExpiresAt, e.Format, contentSha256 = sha, warnings = r.Warnings });
    }

    [HttpGet("payroll/periods/{id:guid}/exports")]
    public async Task<IActionResult> Exports(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var list = await _db.PayrollExports.AsNoTracking().Where(e => e.PeriodId == id).OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        return Ok(list.Select(e => new
        {
            e.Id, e.Kind, e.FileName, e.RowCount, e.SingleUse, e.CreatedBy, e.CreatedAt, e.ExpiresAt, e.DownloadedAt, e.DownloadedBy,
            e.DownloadCount, available = e.Cipher is not null && e.ExpiresAt > DateTimeOffset.UtcNow, e.PurgedAt, e.Format, e.ContentSha256,
        }));
    }

    [HttpGet("payroll/exports/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var e = await _db.PayrollExports.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (e.Cipher is null || e.ExpiresAt <= DateTimeOffset.UtcNow)
            return StatusCode(410, new { message = e.SingleUse && e.DownloadedAt is not null ? "Bu dosya bir kez indirildi ve silindi; gerekirse yeniden üretin." : "Dosyanın süresi doldu ve silindi; yeniden üretin.", code = "export_gone" });
        var plain = ExportCrypto.Decrypt(e.Cipher);
        e.DownloadCount++;
        e.DownloadedAt = DateTimeOffset.UtcNow;
        e.DownloadedBy = UserName;
        if (e.SingleUse) { e.Cipher = null; e.PurgedAt = DateTimeOffset.UtcNow; }
        await _db.SaveChangesAsync(ct);
        var trace = CompensationService.Auditing.TraceCode.New();
        await AuditAsync("PayrollExport", e.Id.ToString(), e.Kind is "SgkAphb" or "SgkHires" or "Bank" ? "Exported" : "Downloaded",
            new { field = e.Kind, e.FileName, e.RowCount, traceCode = trace, sha256 = e.ContentSha256 }, e.TenantSlug);
        // Banka/SGK/muhasebe dosyaları dış sistemlerin biçimindedir; içine filigran satırı eklenmez.
        // İz kodu yalnızca denetim kaydında ve yanıt başlığında taşınır.
        Response.Headers["X-HR360-Trace"] = trace;
        return File(plain, e.ContentType + (e.ContentType.StartsWith("text/") ? "; charset=utf-8" : ""), e.FileName);
    }

    /* ============================================================ Y11 avans ve borç */

    public record AdvanceInput(Guid? EmployeeId, string Kind, decimal Amount, int Installments, int? StartYear, int? StartMonth, string? Reason);

    private async Task<decimal?> CurrentSalaryAsync(Guid employeeId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)); // Türkiye saati (UTC+3)
        return await _db.Records.AsNoTracking().Where(r => r.EmployeeId == employeeId && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today))
            .OrderByDescending(r => r.EffectiveFrom).Select(r => (decimal?)r.BaseSalary).FirstOrDefaultAsync(ct);
    }

    [HttpPost("advances")]
    public async Task<IActionResult> RequestAdvance([FromBody] AdvanceInput body, CancellationToken ct)
    {
        var me = await MyEmployeeIdAsync(ct);
        var emp = body.EmployeeId ?? me;
        if (emp is null) return Forbid();
        if (emp != me && !IsPayrollAdmin) return Forbid();
        if (body.Kind is not ("Advance" or "Loan")) return BadRequest(new { message = "Tür avans ya da borç olmalı" });
        if (body.Amount is <= 0 or > 10_000_000) return BadRequest(new { message = "Tutar sıfırdan büyük olmalı" });
        if (body.Installments is < 1 or > 24) return BadRequest(new { message = "Taksit sayısı 1–24 olmalı" });
        if (body.Reason is { Length: > 300 }) return BadRequest(new { message = "Açıklama en fazla 300 karakter olabilir" });
        if (await _db.Advances.AnyAsync(a => a.EmployeeId == emp && a.Status == AdvanceStatus.Pending, ct))
            return Conflict(new { message = "Karar bekleyen bir talebiniz var" });
        var next = DateTime.UtcNow.AddMonths(1);
        var a = new SalaryAdvance
        {
            EmployeeId = emp.Value, Kind = body.Kind, Amount = Math.Round(body.Amount, 2), Installments = body.Installments,
            StartYear = body.StartYear ?? next.Year, StartMonth = body.StartMonth ?? next.Month,
            Reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim(),
        };
        if (a.StartMonth is < 1 or > 12) return BadRequest(new { message = "Geçersiz başlangıç ayı" });
        _db.Advances.Add(a);
        await _db.SaveChangesAsync(ct);
        return Ok(AdvanceView(a, null));
    }

    private static object AdvanceView(SalaryAdvance a, decimal? salary) => new
    {
        a.Id, a.EmployeeId, a.Kind, a.Amount, a.Installments, a.StartYear, a.StartMonth, a.Reason, status = a.Status.ToString(),
        a.RepaidAmount, remaining = a.Amount - a.RepaidAmount, installment = Exporters.Installment(a.Amount, a.Installments, 0),
        a.DecidedBy, a.DecisionNote, a.DecidedAt, a.CreatedAt,
        // Taksit brüt maaşın %25'ini aşıyorsa uyarı (İK kararında dikkate alınır).
        installmentShare = salary is > 0 ? Math.Round(Exporters.Installment(a.Amount, a.Installments, 0) / salary.Value, 3) : (decimal?)null,
    };

    /// <summary>Çalışan kendi taleplerini; bordro yetkilisi tümünü görür.</summary>
    [HttpGet("advances")]
    public async Task<IActionResult> Advances([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.Advances.AsNoTracking();
        if (!IsPayrollAdmin)
        {
            var me = await MyEmployeeIdAsync(ct);
            if (me is null) return Ok(Array.Empty<object>());
            q = q.Where(a => a.EmployeeId == me);
        }
        if (Enum.TryParse<AdvanceStatus>(status, true, out var st)) q = q.Where(a => a.Status == st);
        var list = await q.OrderByDescending(a => a.CreatedAt).Take(500).ToListAsync(ct);
        var result = new List<object>();
        foreach (var a in list)
            result.Add(AdvanceView(a, IsPayrollAdmin && a.Status == AdvanceStatus.Pending ? await CurrentSalaryAsync(a.EmployeeId, ct) : null));
        return Ok(result);
    }

    public record DecideAdvanceInput(bool Approve, string? Note);

    [HttpPost("advances/{id:guid}/decide")]
    public async Task<IActionResult> DecideAdvance(Guid id, [FromBody] DecideAdvanceInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var a = await _db.Advances.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (a.Status != AdvanceStatus.Pending) return Conflict(new { message = "Talep zaten karara bağlanmış" });
        if (a.EmployeeId == await MyEmployeeIdAsync(ct)) return StatusCode(403, new { message = "Kendi talebinize karar veremezsiniz" });
        if (!body.Approve && string.IsNullOrWhiteSpace(body.Note)) return BadRequest(new { message = "Ret gerekçesi yazın" });
        a.Status = body.Approve ? AdvanceStatus.Approved : AdvanceStatus.Rejected;
        a.DecidedBy = UserName; a.DecisionNote = body.Note?.Trim(); a.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("SalaryAdvance", a.Id.ToString(), body.Approve ? "Approved" : "Rejected", new { a.Installments }, a.TenantSlug);
        // Bildirimde tutar yazmaz (anlık bildirim/e-posta önizlemesinde görünmesin).
        await Notify(a.EmployeeId, body.Approve ? "Avans talebiniz onaylandı" : "Avans talebiniz reddedildi",
            body.Approve ? "Taksitler bordronuzdan kesinti olarak düşülecek. Ayrıntılar: Bordrolarım › Avanslar." : $"Gerekçe: {a.DecisionNote}", "compensation.advance", ct);
        return Ok(AdvanceView(a, null));
    }

    [HttpPost("advances/{id:guid}/cancel")]
    public async Task<IActionResult> CancelAdvance(Guid id, CancellationToken ct)
    {
        var a = await _db.Advances.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (a.EmployeeId != await MyEmployeeIdAsync(ct) && !IsPayrollAdmin) return NotFound();
        if (a.Status != AdvanceStatus.Pending) return Conflict(new { message = "Yalnızca bekleyen talep iptal edilebilir" });
        a.Status = AdvanceStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        return Ok(AdvanceView(a, null));
    }

    /* ============================================================ Y13 esnek yan haklar */

    public record PlanInput(int Year, decimal BudgetPerEmployee, DateOnly WindowStart, DateOnly WindowEnd);
    public record OptionInput(string Name, string Category, decimal AnnualCost, string? Description, bool IsActive = true);
    private static readonly string[] BenefitCategories = { "Meal", "Transport", "Health", "Wellness", "Education", "Other" };

    [HttpGet("benefits/{year:int}")]
    public async Task<IActionResult> Benefits(int year, CancellationToken ct)
    {
        var plan = await _db.BenefitPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Year == year, ct);
        if (plan is null) return Ok(new { plan = (object?)null, options = Array.Empty<object>(), election = (object?)null });
        var options = await _db.BenefitOptions.AsNoTracking().Where(o => o.PlanId == plan.Id && (o.IsActive || IsPayrollAdmin)).OrderBy(o => o.Category).ThenBy(o => o.Name).ToListAsync(ct);
        var me = await MyEmployeeIdAsync(ct);
        var el = me is null ? null : await _db.BenefitElections.AsNoTracking().FirstOrDefaultAsync(e => e.PlanId == plan.Id && e.EmployeeId == me, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)); // Türkiye saati (UTC+3)
        object? summary = null;
        if (IsPayrollAdmin)
        {
            var all = await _db.BenefitElections.AsNoTracking().Where(e => e.PlanId == plan.Id).ToListAsync(ct);
            var counts = all.SelectMany(e => JsonSerializer.Deserialize<List<Guid>>(e.OptionIdsJson) ?? new()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            summary = new { elections = all.Count, total = all.Sum(e => e.Total), byOption = options.Select(o => new { o.Id, o.Name, count = counts.GetValueOrDefault(o.Id) }) };
        }
        return Ok(new
        {
            plan = new { plan.Id, plan.Year, plan.BudgetPerEmployee, plan.WindowStart, plan.WindowEnd, open = today >= plan.WindowStart && today <= plan.WindowEnd },
            options = options.Select(o => new { o.Id, o.Name, o.Category, o.AnnualCost, o.Description, o.IsActive }),
            election = el is null ? null : new { optionIds = JsonSerializer.Deserialize<List<Guid>>(el.OptionIdsJson), el.Total, el.UpdatedAt },
            summary,
        });
    }

    [HttpPut("benefits/plan")]
    public async Task<IActionResult> SavePlan([FromBody] PlanInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        if (body.BudgetPerEmployee is < 0 or > 10_000_000 || body.WindowEnd < body.WindowStart || body.Year is < 2020 or > 2100)
            return BadRequest(new { message = "Geçersiz plan" });
        var p = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.Year == body.Year, ct);
        if (p is null) { p = new BenefitPlan { Year = body.Year }; _db.BenefitPlans.Add(p); }
        p.BudgetPerEmployee = body.BudgetPerEmployee; p.WindowStart = body.WindowStart; p.WindowEnd = body.WindowEnd; p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.Id, p.Year });
    }

    [HttpPost("benefits/{year:int}/options")]
    public async Task<IActionResult> AddOption(int year, [FromBody] OptionInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(p => p.Year == year, ct);
        if (plan is null) return NotFound(new { message = "Önce yılın planını oluşturun" });
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120 || !BenefitCategories.Contains(body.Category) || body.AnnualCost is < 0 or > 10_000_000)
            return BadRequest(new { message = "Geçersiz seçenek" });
        var o = new BenefitOption { PlanId = plan.Id, Name = body.Name.Trim(), Category = body.Category, AnnualCost = body.AnnualCost, Description = body.Description?.Trim(), IsActive = body.IsActive };
        _db.BenefitOptions.Add(o);
        await _db.SaveChangesAsync(ct);
        return Ok(o);
    }

    [HttpPut("benefits/options/{id:guid}")]
    public async Task<IActionResult> UpdateOption(Guid id, [FromBody] OptionInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var o = await _db.BenefitOptions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        if (string.IsNullOrWhiteSpace(body.Name) || !BenefitCategories.Contains(body.Category) || body.AnnualCost < 0) return BadRequest(new { message = "Geçersiz seçenek" });
        o.Name = body.Name.Trim(); o.Category = body.Category; o.AnnualCost = body.AnnualCost; o.Description = body.Description?.Trim(); o.IsActive = body.IsActive;
        await _db.SaveChangesAsync(ct);
        return Ok(o);
    }

    public record ElectInput(List<Guid> OptionIds);

    /// <summary>
    /// Çalışanın seçimi: yalnızca seçim penceresinde, bütçe içinde. KVKK: özel sağlık sigortası
    /// seçilse bile sağlık beyanı istenmez; seçim yalnızca seçenek kimliklerinden oluşur.
    /// </summary>
    [HttpPut("benefits/{year:int}/election")]
    public async Task<IActionResult> Elect(int year, [FromBody] ElectInput body, CancellationToken ct)
    {
        var me = await MyEmployeeIdAsync(ct);
        if (me is null) return Forbid();
        var plan = await _db.BenefitPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Year == year, ct);
        if (plan is null) return NotFound();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)); // Türkiye saati (UTC+3)
        if (today < plan.WindowStart || today > plan.WindowEnd) return BadRequest(new { message = "Seçim penceresi kapalı", code = "window_closed" });
        var ids = (body.OptionIds ?? new()).Distinct().ToList();
        var options = await _db.BenefitOptions.AsNoTracking().Where(o => o.PlanId == plan.Id && o.IsActive && ids.Contains(o.Id)).ToListAsync(ct);
        if (options.Count != ids.Count) return BadRequest(new { message = "Geçersiz seçenek" });
        if (options.GroupBy(o => o.Category).Any(g => g.Count() > 1)) return BadRequest(new { message = "Her kategoriden en fazla bir seçenek seçilebilir" });
        var total = options.Sum(o => o.AnnualCost);
        if (total > plan.BudgetPerEmployee) return BadRequest(new { message = $"Seçimler bütçeyi aşıyor ({total:0.##} > {plan.BudgetPerEmployee:0.##})", code = "over_budget" });
        var el = await _db.BenefitElections.FirstOrDefaultAsync(e => e.PlanId == plan.Id && e.EmployeeId == me, ct);
        if (el is null) { el = new BenefitElection { PlanId = plan.Id, EmployeeId = me.Value }; _db.BenefitElections.Add(el); }
        el.OptionIdsJson = JsonSerializer.Serialize(ids); el.Total = total; el.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { optionIds = ids, total, remaining = plan.BudgetPerEmployee - total });
    }

    /* ============================================================ Y21 zam dönemi */

    public record CycleInput(string Name, int Year, decimal BudgetPercent, DateOnly EffectiveDate);

    [HttpGet("raise-cycles")]
    public async Task<IActionResult> Cycles(CancellationToken ct)
    {
        if (!IsManager) return Forbid();
        var cycles = await _db.RaiseCycles.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        if (!IsPayrollAdmin) cycles = cycles.Where(c => c.Status != RaiseCycleStatus.Draft).ToList();
        return Ok(cycles.Select(c => new { c.Id, c.Name, c.Year, c.BudgetPercent, c.EffectiveDate, status = c.Status.ToString(), c.AppliedAt }));
    }

    [HttpPost("raise-cycles")]
    public async Task<IActionResult> CreateCycle([FromBody] CycleInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(body.Name) || body.BudgetPercent is < 0 or > 100) return BadRequest(new { message = "Geçersiz zam dönemi" });
        // Makul yıl aralığı: geçen yıl .. iki yıl sonrası (1900 gibi hatalı girişler reddedilir).
        var thisYear = DateTime.UtcNow.AddHours(3).Year;
        if (body.Year < thisYear - 1 || body.Year > thisYear + 2)
            return BadRequest(new { message = $"Zam dönemi yılı {thisYear - 1}–{thisYear + 2} aralığında olmalı" });
        var c = new RaiseCycle { Name = body.Name.Trim(), Year = body.Year, BudgetPercent = body.BudgetPercent, EffectiveDate = body.EffectiveDate, CreatedBy = UserName };
        _db.RaiseCycles.Add(c);
        await _db.SaveChangesAsync(ct);
        return Ok(new { c.Id, c.Name, status = c.Status.ToString() });
    }

    public record CycleStatusInput(string Status);

    [HttpPost("raise-cycles/{id:guid}/status")]
    public async Task<IActionResult> SetCycleStatus(Guid id, [FromBody] CycleStatusInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var c = await _db.RaiseCycles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (!Enum.TryParse<RaiseCycleStatus>(body.Status, out var st) || c.AppliedAt is not null) return BadRequest(new { message = "Geçersiz durum" });
        c.Status = st;
        await _db.SaveChangesAsync(ct);
        return Ok(new { c.Id, status = c.Status.ToString() });
    }

    /// <summary>
    /// Zam dönemini siler: yalnızca öneri girilmemiş dönem (taslak ya da boş açık dönem).
    /// Uygulanmış dönem ücret kayıtlarına dönüştüğünden silinemez.
    /// </summary>
    [HttpDelete("raise-cycles/{id:guid}")]
    public async Task<IActionResult> DeleteCycle(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var c = await _db.RaiseCycles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.AppliedAt is not null) return Conflict(new { message = "Uygulanmış zam dönemi silinemez" });
        if (await _db.RaiseProposals.AnyAsync(p => p.CycleId == id, ct))
            return Conflict(new { message = "Bu dönemde zam önerisi var; dönem silinemez. Önerilere kapatmak için durumunu değiştirin." });
        _db.RaiseCycles.Remove(c);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("RaiseCycle", id.ToString(), "Deleted", new { c.Name, c.Year, status = c.Status.ToString() }, c.TenantSlug);
        return NoContent();
    }

    /// <summary>
    /// Kapsamdaki çalışanlar: yönetici yalnızca kendi bölümündekileri (bölüm başı olduğu), İK
    /// herkesi görür. Mevcut ücret ve bant konumu gösterilir; görüntüleme erişim kaydına yazılır.
    /// </summary>
    [HttpGet("raise-cycles/{id:guid}/worksheet")]
    public async Task<IActionResult> Worksheet(Guid id, CancellationToken ct)
    {
        if (!IsManager) return Forbid();
        var c = await _db.RaiseCycles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || (c.Status == RaiseCycleStatus.Draft && !IsPayrollAdmin)) return NotFound();
        var me = await MyEmployeeIdAsync(ct);
        var people = (await PeopleRowsAsync(ct)).Where(p => p.Status != "Terminated").ToList();
        var scope = IsPayrollAdmin ? people : people.Where(p => p.HeadId == me && p.Id != me).ToList();
        var ids = scope.Select(p => p.Id).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)); // Türkiye saati (UTC+3)
        var records = await _db.Records.AsNoTracking().Where(r => ids.Contains(r.EmployeeId) && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today)).ToListAsync(ct);
        var current = records.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First());
        var bands = await _db.SalaryBands.AsNoTracking().Where(b => b.Year == c.Year || b.Year == c.Year - 1).ToListAsync(ct);
        var proposals = await _db.RaiseProposals.AsNoTracking().Where(p => p.CycleId == id && ids.Contains(p.EmployeeId)).ToDictionaryAsync(p => p.EmployeeId, ct);
        await AuditAsync("RaiseCycle", id.ToString(), "SensitiveViewed", new { field = "salaryWorksheet", count = scope.Count }, c.TenantSlug);
        var rows = scope.Where(p => current.ContainsKey(p.Id)).Select(p =>
        {
            var r = current[p.Id];
            var band = bands.Where(b => b.Grade == r.Grade).OrderByDescending(b => b.Year).FirstOrDefault();
            proposals.TryGetValue(p.Id, out var pr);
            // Reddedilen öneri artış sayılmaz: bant kontrolü ve bütçe kullanımı mevcut ücretle yapılır.
            var proposed = pr is not null && pr.Status != RaiseProposalStatus.Rejected ? pr.ProposedSalary : r.BaseSalary;
            return new
            {
                employeeId = p.Id, name = $"{p.FirstName} {p.LastName}", department = p.Department, grade = r.Grade, currentSalary = r.BaseSalary, r.Currency,
                band = band is null ? null : new { band.MinAmount, band.MidAmount, band.MaxAmount },
                compaRatio = band is { MidAmount: > 0 } ? Math.Round(r.BaseSalary / band.MidAmount, 2) : (decimal?)null,
                proposal = pr is null ? null : new { pr.Id, pr.ProposedPercent, pr.ProposedSalary, pr.Note, status = pr.Status.ToString(), pr.ProposedBy, pr.DecidedBy },
                outOfBand = band is not null && (proposed < band.MinAmount || proposed > band.MaxAmount),
            };
        }).OrderBy(x => x.department).ThenBy(x => x.name).ToList();
        var budget = rows.Sum(x => x.currentSalary) * c.BudgetPercent / 100m;
        // Önerilen artış: yalnızca bekleyen, onaylanan (ve uygulanan) öneriler; reddedilenler sayılmaz.
        var used = rows.Where(x => x.proposal is not null && x.proposal.status != nameof(RaiseProposalStatus.Rejected))
            .Sum(x => x.proposal!.ProposedSalary - x.currentSalary);
        return Ok(new { cycle = new { c.Id, c.Name, c.Year, c.BudgetPercent, c.EffectiveDate, status = c.Status.ToString() }, rows, budget = Math.Round(budget, 2), used = Math.Round(used, 2) });
    }

    public record ProposalInput(Guid EmployeeId, decimal ProposedPercent, string? Note);

    [HttpPut("raise-cycles/{id:guid}/proposals")]
    public async Task<IActionResult> Propose(Guid id, [FromBody] ProposalInput body, CancellationToken ct)
    {
        if (!IsManager) return Forbid();
        var c = await _db.RaiseCycles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != RaiseCycleStatus.Open) return Conflict(new { message = "Zam dönemi öneriye açık değil" });
        if (body.ProposedPercent is < 0 or > 100) return BadRequest(new { message = "Zam oranı %0–100 olmalı" });
        var me = await MyEmployeeIdAsync(ct);
        if (body.EmployeeId == me) return StatusCode(403, new { message = "Kendinize zam öneremezsiniz" });
        if (!IsPayrollAdmin)
        {
            var row = (await PeopleRowsAsync(ct)).FirstOrDefault(p => p.Id == body.EmployeeId);
            if (row is null || row.HeadId != me) return StatusCode(403, new { message = "Yalnızca kendi bölümünüzdeki çalışanlar için öneri yapabilirsiniz" });
        }
        var salary = await CurrentSalaryAsync(body.EmployeeId, ct);
        if (salary is null) return BadRequest(new { message = "Çalışanın geçerli ücret kaydı yok" });
        var p = await _db.RaiseProposals.FirstOrDefaultAsync(x => x.CycleId == id && x.EmployeeId == body.EmployeeId, ct);
        if (p is { Status: RaiseProposalStatus.Approved or RaiseProposalStatus.Applied }) return Conflict(new { message = "Öneri onaylanmış; değiştirilemez" });
        if (p is null) { p = new RaiseProposal { CycleId = id, EmployeeId = body.EmployeeId }; _db.RaiseProposals.Add(p); }
        p.CurrentSalary = salary.Value; p.ProposedPercent = body.ProposedPercent;
        p.ProposedSalary = Math.Round(salary.Value * (1 + body.ProposedPercent / 100m), 2);
        p.Note = body.Note?.Trim(); p.Status = RaiseProposalStatus.Proposed; p.ProposedBy = UserName; p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.Id, p.ProposedPercent, p.ProposedSalary, status = p.Status.ToString() });
    }

    public record DecideProposalsInput(List<Guid> ProposalIds, bool Approve);

    [HttpPost("raise-cycles/{id:guid}/decide")]
    public async Task<IActionResult> DecideProposals(Guid id, [FromBody] DecideProposalsInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var me = await MyEmployeeIdAsync(ct);
        var list = await _db.RaiseProposals.Where(p => p.CycleId == id && body.ProposalIds.Contains(p.Id) && p.Status == RaiseProposalStatus.Proposed).ToListAsync(ct);
        foreach (var p in list.Where(p => p.EmployeeId != me))
        {
            p.Status = body.Approve ? RaiseProposalStatus.Approved : RaiseProposalStatus.Rejected;
            p.DecidedBy = UserName; p.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { decided = list.Count(p => p.EmployeeId != me) });
    }

    /// <summary>Onaylı önerileri yürürlük tarihiyle yeni ücret kaydına dönüştürür ve dönemi kapatır.</summary>
    [HttpPost("raise-cycles/{id:guid}/apply")]
    public async Task<IActionResult> Apply(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var c = await _db.RaiseCycles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.AppliedAt is not null) return Conflict(new { message = "Zam dönemi zaten uygulandı" });
        if (c.Status != RaiseCycleStatus.Open) return Conflict(new { message = "Yalnızca önerilere açık zam dönemi uygulanabilir" });
        var approved = await _db.RaiseProposals.Where(p => p.CycleId == id && p.Status == RaiseProposalStatus.Approved).ToListAsync(ct);
        foreach (var p in approved)
        {
            var open = await _db.Records.Where(r => r.EmployeeId == p.EmployeeId && r.EffectiveTo == null && r.EffectiveFrom < c.EffectiveDate).ToListAsync(ct);
            foreach (var r in open) r.EffectiveTo = c.EffectiveDate.AddDays(-1);
            var last = open.OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
            _db.Records.Add(new CompensationRecord
            {
                EmployeeId = p.EmployeeId, BaseSalary = p.ProposedSalary, Currency = last?.Currency ?? "TRY", Grade = last?.Grade,
                Reason = CompensationChangeReason.AnnualIncrease, EffectiveFrom = c.EffectiveDate, Note = c.Name,
            });
            p.Status = RaiseProposalStatus.Applied;
        }
        c.AppliedAt = DateTimeOffset.UtcNow;
        c.Status = RaiseCycleStatus.Closed;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("RaiseCycle", id.ToString(), "Applied", new { count = approved.Count }, c.TenantSlug);
        foreach (var p in approved)
            await Notify(p.EmployeeId, "Ücret güncellemesi", $"{c.Name} kapsamında ücretiniz {c.EffectiveDate:dd.MM.yyyy} itibarıyla güncellendi. Ayrıntı için İK ile görüşebilirsiniz.", "compensation.raise", ct);
        return Ok(new { applied = approved.Count });
    }

    /// <summary>Bölüm bazında özet: 5 kişiden az gruplar gizlenir (bireysel ücret çıkarılamasın).</summary>
    [HttpGet("raise-cycles/{id:guid}/summary")]
    public async Task<IActionResult> CycleSummary(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var proposals = await _db.RaiseProposals.AsNoTracking().Where(p => p.CycleId == id && p.Status != RaiseProposalStatus.Rejected).ToListAsync(ct);
        var people = (await PeopleRowsAsync(ct)).ToDictionary(p => p.Id);
        var groups = proposals.GroupBy(p => people.TryGetValue(p.EmployeeId, out var r) ? r.Department ?? "—" : "—")
            .Select(g => g.Count() < 5
                ? new { department = g.Key, count = g.Count(), avgPercent = (decimal?)null, hidden = true }
                : new { department = g.Key, count = g.Count(), avgPercent = (decimal?)Math.Round(g.Average(p => p.ProposedPercent), 2), hidden = false })
            .OrderBy(x => x.department).ToList();
        return Ok(new { groups, overallAvg = proposals.Count >= 5 ? Math.Round(proposals.Average(p => p.ProposedPercent), 2) : (decimal?)null, count = proposals.Count });
    }
}

/// <summary>Süresi dolan dışa aktarım dosyalarının içeriğini siler (kayıt kalır). Saatte bir.</summary>
public sealed class ExportPurgeWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ExportPurgeWorker> _log;
    public ExportPurgeWorker(IServiceProvider sp, ILogger<ExportPurgeWorker> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CompensationDbContext>();
                var n = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE compensation_payroll_exports SET \"Cipher\" = NULL, \"PurgedAt\" = now() WHERE \"Cipher\" IS NOT NULL AND \"ExpiresAt\" <= now()", ct);
                if (n > 0) _log.LogInformation("{Count} dışa aktarım dosyasının içeriği silindi", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Dışa aktarım temizliği başarısız"); }
            await Task.Delay(TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable("EXPORT_PURGE_MINUTES"), out var m) ? Math.Max(1, m) : 60), ct);
        }
    }
}
