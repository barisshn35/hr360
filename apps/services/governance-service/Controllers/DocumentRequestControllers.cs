using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;

namespace GovernanceService.Controllers;

/// <summary>
/// Çalışan belge talebi (çalışma belgesi, maaş yazısı...). Şablonu İK "çalışan talep edebilir"
/// olarak işaretler; onay gerektirmeyen belge hemen, gerektiren İK onayından sonra düzenlenir.
///
/// KVKK: Belgede yalnızca şablonun gerektirdiği alanlar bulunur; düzenlenen belge şifreli
/// saklanır ve yalnızca çalışana ve İK'ya açılır. Doğrulama sayfası kişisel veri göstermez
/// (yalnızca belge türü, tarih ve baş harfler).
/// </summary>
[Route("api/documents/requests")]
[Authorize]
public class DocumentRequestsController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly Notifier _notifier;
    public DocumentRequestsController(GovernanceDbContext db, Notifier notifier) { _db = db; _notifier = notifier; }

    private static string NewCode()
    {
        const string A = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // karışmayan karakterler
        var b = RandomNumberGenerator.GetBytes(10);
        var s = new string(b.Select(x => A[x % A.Length]).ToArray());
        return $"{s[..5]}-{s[5..]}";
    }

    /// <summary>Oturumda şirket yoksa (platform yöneticisi) çalışan uçları boş döner.</summary>
    private bool NoTenant => string.IsNullOrEmpty(HttpContext.RequestServices.GetRequiredService<Tenancy.ITenantContext>().TenantSlug);

    private static string VerifyUrl(string code) => $"{ChatService.PublicOrigin}/belge-dogrula/{code}";

    private static object Dto(DocumentRequest r) => new
    {
        r.Id, r.EmployeeId, r.TemplateId, r.TemplateName, r.Purpose, r.Status, r.DecisionNote, r.CreatedAt, r.IssuedAt,
        verificationCode = r.Status == "Issued" ? r.VerificationCode : null,
    };

    /// <summary>Çalışanın talep edebileceği şablonlar.</summary>
    [HttpGet("templates")]
    public async Task<IActionResult> Templates(CancellationToken ct) => NoTenant ? Ok(Array.Empty<object>()) :
        Ok(await _db.DocTemplates.AsNoTracking().Where(t => t.SelfService).OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Category, t.RequiresApproval }).ToListAsync(ct));

    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (NoTenant) return Ok(Array.Empty<object>());
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var rows = await _db.DocumentRequests.AsNoTracking().Where(r => r.EmployeeId == me.Id).OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var signed = (await Db.QueryAsync("SELECT \"DocumentId\" FROM governance_signatures WHERE \"TenantSlug\" = $1 AND \"SignerEmployeeId\" = $2 AND \"DocumentType\" = 'DocumentRequest'",
            x => x.GetGuid(0), ct, Tenant, me.Id)).ToHashSet();
        return Ok(rows.Select(r => new
        {
            r.Id, r.EmployeeId, r.TemplateId, r.TemplateName, r.Purpose, r.Status, r.DecisionNote, r.CreatedAt, r.IssuedAt,
            verificationCode = r.Status == "Issued" ? r.VerificationCode : null, signed = signed.Contains(r.Id),
        }));
    }

    [HttpGet]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.DocumentRequests.AsNoTracking();
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(300).ToListAsync(ct);
        return Ok(rows.Select(Dto));
    }

    public record CreateInput(Guid TemplateId, string? Purpose);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return StatusCode(403, new { message = L("Hesabınıza bağlı çalışan kaydı yok", "Your account is not linked to an employee record") });
        var t = await _db.DocTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == body.TemplateId && x.SelfService, ct);
        if (t is null) return NotFound(new { message = L("Bu belge talep edilemiyor", "This document cannot be requested") });
        if (body.Purpose is { Length: > 200 }) return BadRequest(new { message = L("Kullanım amacı en fazla 200 karakter olabilir", "The purpose can be at most 200 characters") });
        if (await _db.DocumentRequests.CountAsync(r => r.EmployeeId == me.Id && r.CreatedAt > DateTime.UtcNow.AddDays(-1), ct) >= 10)
            return StatusCode(429, new { message = L("Günde en fazla 10 belge talep edilebilir", "At most 10 documents can be requested per day") });
        var r = new DocumentRequest
        {
            EmployeeId = me.Id, TemplateId = t.Id, TemplateName = t.Name,
            Purpose = string.IsNullOrWhiteSpace(body.Purpose) ? null : body.Purpose.Trim(),
        };
        _db.DocumentRequests.Add(r);
        if (!t.RequiresApproval) await IssueAsync(r, t, "otomatik", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(r));
    }

    private Task IssueAsync(DocumentRequest r, DocTemplate t, string by, CancellationToken ct) => IssueCoreAsync(Db, People, Tenant, r, t, by, ct);

    /// <summary>Belgeyi üretir (web ve sohbet botu aynı kuralı kullanır; bot belge içeriğini asla mesaja yazmaz).</summary>
    public static async Task IssueCoreAsync(Sql sql, PeopleDirectory people, string tenant, DocumentRequest r, DocTemplate t, string by, CancellationToken ct)
    {
        // Kişinin kendi belgesi: ücret yer tutucusu yalnızca kendi ücretini gösterir.
        var docs = await DocRenderer.RenderAsync(sql, people, tenant, t.Body, new[] { r.EmployeeId }, canSeePay: true, ct);
        if (docs.Count == 0) throw new InvalidOperationException("Çalışan bulunamadı");
        r.VerificationCode = NewCode();
        var issued = DateTime.UtcNow;
        var footer = $"""<hr style="margin-top:40px"/><p style="font-size:9pt;color:#555">Doğrulama kodu: <b>{r.VerificationCode}</b> · {WebUtility.HtmlEncode(VerifyUrl(r.VerificationCode))}<br/>Düzenlenme: {issued.AddHours(3):dd.MM.yyyy HH:mm}{(r.Purpose is null ? "" : " · Kullanım amacı: " + WebUtility.HtmlEncode(r.Purpose))}</p>""";
        var html = docs[0].Html + footer;
        r.DocumentEnc = SecretBox.Protect(html);
        r.DocumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
        r.Status = "Issued";
        r.IssuedAt = issued;
        r.DecidedBy = by;
    }

    public record DecideInput(bool Approve, string? Note);

    [HttpPost("{id:guid}/decide")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideInput body, CancellationToken ct)
    {
        var r = await _db.DocumentRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (r.Status != "Pending") return Conflict(new { message = L("Talep zaten karara bağlanmış", "The request has already been decided") });
        var me = await MyPersonAsync(ct);
        if (me?.Id == r.EmployeeId) return StatusCode(403, new { message = L("Kendi belge talebinize karar veremezsiniz", "You cannot decide on your own document request") });
        if (body.Approve)
        {
            var t = await _db.DocTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TemplateId, ct);
            if (t is null) return Conflict(new { message = L("Şablon silinmiş; talep düzenlenemez", "The template was deleted; the document cannot be issued") });
            await IssueAsync(r, t, Me.Name, ct);
        }
        else
        {
            r.Status = "Rejected";
            r.DecisionNote = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()[..Math.Min(body.Note.Trim().Length, 300)];
            r.DecidedBy = Me.Name;
        }
        await _db.SaveChangesAsync(ct);
        await _notifier.LocalizedAsync(Tenant, r.EmployeeId,
            body.Approve ? $"Belgeniz hazır: {r.TemplateName}" : $"Belge talebiniz reddedildi: {r.TemplateName}",
            body.Approve ? $"Your document is ready: {r.TemplateName}" : $"Your document request was rejected: {r.TemplateName}",
            body.Approve ? "Günlük iş › Belge talebi ekranından indirebilir, tek kullanımlık kodla imzalayabilirsiniz." : (r.DecisionNote ?? "Ayrıntı için İK ile görüşün."),
            body.Approve ? "You can download it, and sign it with a one-time code, under Daily work › Document request." : (r.DecisionNote ?? "Please contact HR for details."),
            "document.request", ct);
        return Ok(Dto(r));
    }

    /// <summary>Düzenlenen belgenin HTML'i: çalışanın kendisi ya da İK. İK'nın açması erişim kaydına yazılır.</summary>
    [HttpGet("{id:guid}/document")]
    public async Task<IActionResult> Document(Guid id, CancellationToken ct)
    {
        var r = await _db.DocumentRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null || r.Status != "Issued" || r.DocumentEnc is null) return NotFound();
        var me = await MyPersonAsync(ct);
        if (me?.Id != r.EmployeeId)
        {
            if (!Me.IsHr) return NotFound();
            await Db.ExecuteAsync("""
                INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
                VALUES ($1,'governance-service','DocumentRequest',$2,'SensitiveViewed',$3::jsonb,$4,$5,now())
                """, ct, Tenant, r.EmployeeId.ToString(), $"{{\"field\":\"document\",\"template\":{System.Text.Json.JsonSerializer.Serialize(r.TemplateName)}}}", Me.UserId, Me.Name);
        }
        var sig = await DocumentSignatureController.EvidenceAsync(Db, Tenant, r.Id, ct);
        return Ok(new { r.TemplateName, html = SecretBox.Unprotect(r.DocumentEnc), r.VerificationCode, r.IssuedAt,
            signature = sig is null ? null : DocumentSignatureController.View(sig, En) });
    }
}

/// <summary>
/// Herkese açık belge doğrulama (oturum gerekmez). Kişisel veri göstermez: belge türü,
/// düzenlenme tarihi, sahibinin baş harfleri ve şirket adı. IP başına dakikada 20 istek.
/// </summary>
[Route("api/documents/verify")]
[AllowAnonymous]
public class DocumentVerifyController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (DateTime Window, int Count)> Hits = new();
    private readonly GovernanceDbContext _db;
    private readonly Sql _sql;
    public DocumentVerifyController(GovernanceDbContext db, Sql sql) { _db = db; _sql = sql; }

    [HttpGet("{code}")]
    public async Task<IActionResult> Verify(string code, CancellationToken ct)
    {
        var ip = Request.Headers["X-Real-IP"].FirstOrDefault() ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        var now = DateTime.UtcNow;
        var h = Hits.AddOrUpdate(ip, _ => (now, 1), (_, v) => now - v.Window > TimeSpan.FromMinutes(1) ? (now, 1) : (v.Window, v.Count + 1));
        if (h.Count > 20) return StatusCode(429, new { message = "Çok fazla deneme; bir dakika sonra tekrar deneyin" });
        if (Hits.Count > 10_000) Hits.Clear();

        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length != 11) return Ok(new { valid = false });
        var r = await _db.DocumentRequests.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.VerificationCode == code && x.Status == "Issued", ct);
        if (r is null) return Ok(new { valid = false });
        var name = (await _sql.QueryAsync("SELECT \"FirstName\", \"LastName\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2",
            x => (F: x.Str(0) ?? "", L: x.Str(1) ?? ""), ct, r.TenantSlug, r.EmployeeId)).FirstOrDefault();
        var company = (await _sql.QueryAsync("SELECT \"Name\" FROM platform_tenants WHERE \"Slug\" = $1", x => x.Str(0), ct, r.TenantSlug)).FirstOrDefault();
        static string Initial(string s) => s.Length > 0 ? char.ToUpper(s[0], new System.Globalization.CultureInfo("tr-TR")) + "." : "";
        // Y28: imza kanıtı (kişisel veri yok: tarih, yöntem, belge özeti, kanıt özeti ve yasal uyarı).
        var sig = await DocumentSignatureController.EvidenceAsync(_sql, r.TenantSlug, r.Id, ct);
        var en = Request.Headers["X-HR360-Lang"].ToString().StartsWith("en", StringComparison.OrdinalIgnoreCase);
        return Ok(new
        {
            valid = true, document = r.TemplateName, issuedAt = r.IssuedAt, holder = $"{Initial(name.F)} {Initial(name.L)}".Trim(),
            company, hash = r.DocumentHash?[..16],
            signature = sig is null ? null : new
            {
                signed = true, signedAt = sig.SignedAt, method = sig.Method, documentSha256 = sig.DocumentSha256, evidenceSha256 = sig.EvidenceSha256,
                matchesDocument = sig.DocumentSha256 == r.DocumentHash, disclaimer = en ? Signatures.DisclaimerEn : Signatures.DisclaimerTr,
            },
        });
    }
}
