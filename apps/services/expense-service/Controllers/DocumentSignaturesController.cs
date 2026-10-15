using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;
using ExpenseService.Tenancy;

namespace ExpenseService.Controllers;

/// <summary>
/// Y28: OTP ile basit elektronik imza — talep yasam dongusu. IK bir ozluk dokumanini calisana
/// imzaya gonderir/iptal eder; calisan dokumani gorur ve imzalar. Kod (OTP), imza ve kanit
/// governance-service'teki TEK imza motoruna devredilir (DocumentType "HrDocument"; ayni
/// kurallar: 6 hane, 10 dk, 5 hatali deneme, saatte 5 kod, 30 sn bekleme, kod yalnizca HMAC).
/// Basarili imzada governance kanit kimligi talepte (EvidenceRef) tutulur. Bu degisiklikten
/// onceki imzalarin kanitlari expense_signature_evidence'ta SALT OKUNUR kalir.
///
/// Basit elektronik imza — 5070 sayili Kanun kapsaminda nitelikli (guvenli) elektronik imza
/// DEGILDIR. Yanitlarin hepsinde "disclaimer" alani bulunur.
/// </summary>
[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentSignaturesController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ExpenseDbContext _db;
    private readonly ApprovalWorkflowClient _employees;
    private readonly GovernanceSignatureClient _engine;
    private readonly ITenantContext _tenant;
    private readonly ILogger<DocumentSignaturesController> _log;

    public DocumentSignaturesController(ExpenseDbContext db, ApprovalWorkflowClient employees, GovernanceSignatureClient engine,
        ITenantContext tenant, ILogger<DocumentSignaturesController> log)
    {
        _db = db;
        _employees = employees;
        _engine = engine;
        _tenant = tenant;
        _log = log;
    }

    private string? UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
    private string? UserName => User.FindFirst("preferred_username")?.Value ?? User.FindFirst("name")?.Value;
    private string Tenant => _tenant.TenantSlug ?? "";
    private string? ClientIp => Request.Headers["X-Real-IP"].FirstOrDefault() ?? HttpContext.Connection.RemoteIpAddress?.ToString();

    private bool CanManage =>
        User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin") || User.IsInRole("ext-document-manage");

    private IActionResult Fail(int status, string message, string? code = null) =>
        StatusCode(status, new { message, code, disclaimer = SimpleSignature.Disclaimer });

    /// <summary>Imza motorunun hatasini ayni durum/kodla iletir (+ kalan deneme).</summary>
    private IActionResult Fail<T>(GovResult<T> r) => r.AttemptsLeft is { } left
        ? StatusCode(r.Status, new { message = r.Message, code = r.Code, attemptsLeft = left, disclaimer = SimpleSignature.Disclaimer })
        : Fail(r.Status, r.Message ?? "İmza işlemi tamamlanamadı", r.Code);

    private async Task AuditAsync(string entityType, string entityId, string action, object changes)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES (@tenant,'expense-service',@type,@id,@action,@changes::jsonb,@uid,@uname,@corr,@ip,now())",
                new object[]
                {
                    Param("tenant", _tenant.TenantSlug), Param("type", entityType), Param("id", entityId), Param("action", action),
                    Param("changes", JsonSerializer.Serialize(changes, Json)), Param("uid", UserId), Param("uname", UserName),
                    Param("corr", Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier),
                    Param("ip", SimpleSignature.MaskIp(ClientIp)),
                });
        }
        catch (Exception ex) { _log.LogWarning("Denetim kaydi yazilamadi: {Message}", ex.Message); }
    }

    /// <summary>Bildirim (uygulama ici ya da e-posta kanali satiri). Kisisel veri yalnizca gerekli kadar.</summary>
    private async Task NotifyAsync(Guid employeeId, string channel, string? email, string subject, string body, string code,
        string? actionUrl, CancellationToken ct)
    {
        try
        {
            // NOT: ham SQL'de DBNull.Value tur eslemesi bulamiyor; null degerler tipli Npgsql parametresiyle gecer.
            await _db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","ActionUrl","ActionLabel")
                VALUES (@id,@tenant,@emp,@email,@channel,@code,@subject,@body,'Pending',0,now(),@url,@label)
                """, new object[]
                {
                    Param("id", Guid.NewGuid(), NpgsqlTypes.NpgsqlDbType.Uuid), Param("tenant", Tenant), Param("emp", employeeId, NpgsqlTypes.NpgsqlDbType.Uuid),
                    Param("email", email), Param("channel", channel), Param("code", code), Param("subject", subject), Param("body", body),
                    Param("url", actionUrl), Param("label", actionUrl is null ? null : "Belgeyi aç"),
                }, ct);
        }
        catch (Exception ex) { _log.LogWarning("Bildirim yazilamadi: {Message}", ex.Message); }
    }

    private static Npgsql.NpgsqlParameter Param(string name, object? value, NpgsqlTypes.NpgsqlDbType type = NpgsqlTypes.NpgsqlDbType.Text) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static object DocView(Document d) => new
    {
        d.Id, d.EmployeeId, type = d.Type.ToString(), d.FileName, d.StorageKey, d.SizeBytes, d.ContentType, d.UploadedAt, d.SignedAt,
        contentHash = SimpleSignature.DocumentHash(d),
    };

    /// <summary>Kanit: yeni imzalarda governance kaniti (EvidenceRef), eskilerde expense_signature_evidence.</summary>
    private static object? EvidenceView(DocumentSignature s, SignatureEvidence? legacy, IReadOnlyDictionary<Guid, GovEvidence> gov) =>
        s.EvidenceRef is { } r ? (gov.TryGetValue(r, out var g) ? SignatureViews.Evidence(s.Id, g) : null)
        : legacy is null ? null : SignatureViews.Legacy(legacy);

    // "otp" alani kaldirildi: kod durumu artik imza motorunda; istemci kodu her acilista yeniden ister.
    private static object RequestView(DocumentSignature s, Document? d, SignatureEvidence? legacy, IReadOnlyDictionary<Guid, GovEvidence> gov) => new
    {
        s.Id, s.DocumentId, s.EmployeeId, s.Status, s.Message, s.CreatedAt, s.SignedAt, s.CancelledAt,
        requestedDocumentHash = s.DocumentHash,
        otp = (object?)null,
        document = d is null ? null : DocView(d),
        evidence = EvidenceView(s, legacy, gov),
        disclaimer = SimpleSignature.Disclaimer,
    };

    private static readonly IReadOnlyDictionary<Guid, GovEvidence> NoGov = new Dictionary<Guid, GovEvidence>();

    private Task<Dictionary<Guid, GovEvidence>> GovEvidenceAsync(IEnumerable<DocumentSignature> rows, CancellationToken ct) =>
        _engine.EvidenceAsync(Tenant, null, rows.Where(r => r.EvidenceRef is not null).Select(r => r.EvidenceRef!.Value).Distinct().ToList(), ct);

    /// <summary>Imza surumu: dokumanin bu motorla imzalanmis talep sayisi + 1 (yeniden imza = yeni surum).</summary>
    private async Task<int> NextVersionAsync(Guid documentId, CancellationToken ct) =>
        await _db.DocumentSignatures.CountAsync(x => x.DocumentId == documentId && x.Status == SignatureStatus.Signed && x.EvidenceRef != null, ct) + 1;

    /* ------------------------------------------------------------ IK: imzaya gonder */

    public record SignatureRequestInput(string? Message);

    [HttpPost("{id:guid}/signature-requests")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> CreateRequest(Guid id, [FromBody] SignatureRequestInput? body, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return Fail(404, "Doküman bulunamadı");
        if (doc.EmployeeId == Guid.Empty) return Fail(400, "Dokümanın bağlı olduğu çalışan yok");
        if (await _db.DocumentSignatures.AnyAsync(s => s.DocumentId == id && s.Status == SignatureStatus.Pending, ct))
            return Fail(409, "Bu doküman için bekleyen bir imza talebi zaten var", "pending_exists");
        var message = string.IsNullOrWhiteSpace(body?.Message) ? null : body!.Message!.Trim();
        if (message is { Length: > 500 }) return Fail(400, "Not en fazla 500 karakter olabilir");

        var sig = new DocumentSignature
        {
            DocumentId = doc.Id,
            EmployeeId = doc.EmployeeId,
            DocumentHash = SimpleSignature.DocumentHash(doc),
            Message = message,
            RequestedByEmployeeId = await _employees.FindMyEmployeeIdAsync(ct),
            RequestedByUserId = UserId,
        };
        _db.DocumentSignatures.Add(sig);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Fail(409, "Bu doküman için bekleyen bir imza talebi zaten var", "pending_exists"); }

        await NotifyAsync(doc.EmployeeId, "InApp", null, "İmzanızı bekleyen bir belge var",
            "İK bir belgeyi basit elektronik imzanıza gönderdi. Belgeyi İmzalarım sayfasında inceleyip tek kullanımlık kodla imzalayabilirsiniz.",
            "document.sign.request", "/panel/imzalarim", ct);
        return Ok(RequestView(sig, doc, null, NoGov));
    }

    /// <summary>Dokumanin tum imza talepleri ve kanitlari (IK).</summary>
    [HttpGet("{id:guid}/signatures")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> ForDocument(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return Fail(404, "Doküman bulunamadı");
        var rows = await _db.DocumentSignatures.Where(s => s.DocumentId == id).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var ev = await _db.SignatureEvidence.Where(e => ids.Contains(e.SignatureId)).ToDictionaryAsync(e => e.SignatureId, ct);
        var gov = await GovEvidenceAsync(rows, ct);
        if (ev.Count + gov.Count > 0)
            await AuditAsync("SignatureEvidence", id.ToString(), "SensitiveViewed", new { field = "signatureEvidence", count = ev.Count + gov.Count });
        return Ok(new
        {
            document = DocView(doc),
            items = rows.Select(r => RequestView(r, null, ev.GetValueOrDefault(r.Id), gov)),
            disclaimer = SimpleSignature.Disclaimer,
        });
    }

    /// <summary>Durum ozeti (dokuman listesinde rozet icin): dokuman -> son talep durumu.</summary>
    [HttpGet("signature-status")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> StatusSummary([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        var q = _db.DocumentSignatures.AsQueryable();
        if (employeeId.HasValue) q = q.Where(s => s.EmployeeId == employeeId.Value);
        var rows = await q.Select(s => new { s.DocumentId, s.Id, s.Status, s.CreatedAt, s.SignedAt }).ToListAsync(ct);
        return Ok(rows.GroupBy(r => r.DocumentId).Select(g =>
        {
            var last = g.OrderByDescending(x => x.CreatedAt).First();
            return new { documentId = g.Key, signatureId = last.Id, status = last.Status, signedAt = g.Max(x => x.SignedAt) };
        }));
    }

    [HttpDelete("signature-requests/{sid:guid}")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> Cancel(Guid sid, CancellationToken ct)
    {
        var s = await _db.DocumentSignatures.FirstOrDefaultAsync(x => x.Id == sid, ct);
        if (s is null) return Fail(404, "İmza talebi bulunamadı");
        if (s.Status != SignatureStatus.Pending) return Fail(409, "Yalnızca bekleyen talepler iptal edilebilir");
        s.Status = SignatureStatus.Cancelled;
        s.CancelledAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "İmza talebi iptal edildi", disclaimer = SimpleSignature.Disclaimer });
    }

    /* ------------------------------------------------------------ calisan: imzalarim */

    private async Task<(DocumentSignature? Sig, IActionResult? Error)> MineAsync(Guid sid, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return (null, Fail(403, "Çalışan kaydınız bulunamadı"));
        var s = await _db.DocumentSignatures.FirstOrDefaultAsync(x => x.Id == sid, ct);
        // Baskasinin talebi varligi da sizdirilmasin: 404.
        if (s is null || s.EmployeeId != me.Value) return (null, Fail(404, "İmza talebi bulunamadı"));
        return (s, null);
    }

    [HttpGet("signature-requests/mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return Ok(new { items = Array.Empty<object>(), disclaimer = SimpleSignature.Disclaimer });
        var rows = await _db.DocumentSignatures.Where(s => s.EmployeeId == me.Value && s.Status != SignatureStatus.Cancelled)
            .OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);
        var docIds = rows.Select(r => r.DocumentId).Distinct().ToList();
        var docs = await _db.Documents.Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var sids = rows.Select(r => r.Id).ToList();
        var ev = await _db.SignatureEvidence.Where(e => sids.Contains(e.SignatureId)).ToDictionaryAsync(e => e.SignatureId, ct);
        var gov = await GovEvidenceAsync(rows, ct);
        return Ok(new
        {
            items = rows.Where(r => docs.ContainsKey(r.DocumentId))
                .Select(r => RequestView(r, docs[r.DocumentId], ev.GetValueOrDefault(r.Id), gov)),
            disclaimer = SimpleSignature.Disclaimer,
        });
    }

    [HttpGet("signature-requests/{sid:guid}")]
    public async Task<IActionResult> Get(Guid sid, CancellationToken ct)
    {
        DocumentSignature? s;
        if (CanManage)
            s = await _db.DocumentSignatures.FirstOrDefaultAsync(x => x.Id == sid, ct);
        else
        {
            var (mine, err) = await MineAsync(sid, ct);
            if (err is not null) return err;
            s = mine;
        }
        if (s is null) return Fail(404, "İmza talebi bulunamadı");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == s.DocumentId, ct);
        var ev = await _db.SignatureEvidence.FirstOrDefaultAsync(e => e.SignatureId == s.Id, ct);
        return Ok(RequestView(s, doc, ev, await GovEvidenceAsync(new[] { s }, ct)));
    }

    /// <summary>
    /// Tek kullanimlik kod: imza motoru uretir ve bildirimle gonderir (uygulama ici + kayitli
    /// e-posta varsa e-posta; sablon 'signature.otp'). Kod yanitta DONMEZ.
    /// </summary>
    [HttpPost("signature-requests/{sid:guid}/otp")]
    public async Task<IActionResult> SendOtp(Guid sid, CancellationToken ct)
    {
        var (s, err) = await MineAsync(sid, ct);
        if (err is not null) return err;
        if (s!.Status != SignatureStatus.Pending) return Fail(409, "Bu talep artık imzalanamaz", "not_pending");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == s.DocumentId, ct);
        if (doc is null) return Fail(404, "Doküman bulunamadı");
        var r = await _engine.RequestOtpAsync(Tenant, doc.Id, s.EmployeeId, doc.FileName, "InApp+Email", await NextVersionAsync(doc.Id, ct), ct);
        if (!r.Ok) return Fail(r);
        var otp = r.Value!;
        return Ok(new
        {
            message = "Doğrulama kodu bildirimlerinize gönderildi",
            otpId = otp.OtpId,
            channel = otp.Channel,
            expiresAt = otp.ExpiresAt,
            attemptsLeft = otp.MaxAttempts,
            maxAttempts = otp.MaxAttempts,
            sendsLeft = otp.SendsLeft,
            disclaimer = SimpleSignature.Disclaimer,
        });
    }

    public record SignInput(string? Code, bool Accept, Guid? OtpId = null);

    [HttpPost("signature-requests/{sid:guid}/sign")]
    public async Task<IActionResult> Sign(Guid sid, [FromBody] SignInput body, CancellationToken ct)
    {
        var (s, err) = await MineAsync(sid, ct);
        if (err is not null) return err;
        if (!body.Accept) return Fail(400, "İmzalamak için belgeyi okuduğunuzu ve basit elektronik imza açıklamasını onaylamalısınız");
        if (s!.Status != SignatureStatus.Pending) return Fail(409, "Bu talep artık imzalanamaz", "not_pending");

        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == s.DocumentId, ct);
        if (doc is null) return Fail(404, "Doküman bulunamadı");
        // Kod tuketilmeden ONCE: belge imzaya gonderildikten sonra degistiyse imza yok.
        var hash = SimpleSignature.DocumentHash(doc);
        if (hash != s.DocumentHash)
            return Fail(409, "Belge imzaya gönderildikten sonra değişti; İK'dan yeni talep isteyin", "document_changed");

        var r = await _engine.SignAsync(Tenant, doc.Id, s.EmployeeId, body.OtpId, body.Code, hash, await NextVersionAsync(doc.Id, ct),
            ClientIp, UserId, UserName, doc.FileName, ct);
        if (!r.Ok)
        {
            if (r.Code is "otp_invalid" or "otp_locked")
                await AuditAsync("DocumentSignature", s.Id.ToString(), "OtpFailed", new { attemptsLeft = r.AttemptsLeft });
            return Fail(r);
        }
        var ev = r.Value!;
        s.Status = SignatureStatus.Signed;
        s.SignedAt = ev.SignedAt;
        s.EvidenceRef = ev.Id;
        // Ilk imza dokumani kilitler (sonraki yeniden imzalar kilidi degistirmez).
        if (doc.SignedAt is null) doc.SignedAt = ev.SignedAt;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            // Kanit governance'ta yazildi; talep durumu guncellenemedi (eszamanli iptal vb.).
            _log.LogWarning("Imza talebi guncellenemedi ({Id}): {Message}", s.Id, ex.Message);
            return Fail(409, "Bu talep zaten imzalandı", "not_pending");
        }

        if (s.RequestedByEmployeeId is { } hr && hr != s.EmployeeId)
            await NotifyAsync(hr, "InApp", null, "Belge imzalandı",
                "İmzaya gönderdiğiniz bir belge çalışan tarafından basit elektronik imzayla imzalandı.",
                "document.sign.done", "/panel/dokumanlar", ct);
        return Ok(new { message = "Belge imzalandı", evidence = SignatureViews.Evidence(s.Id, ev), disclaimer = SimpleSignature.Disclaimer });
    }
}
