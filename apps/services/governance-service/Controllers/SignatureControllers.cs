using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y28 OTP ile basit elektronik imza — TEK imza motoru (SignatureEngine).
 * - Düzenlenmiş belge talepleri (DocumentRequest): bu dosyadaki web uçları.
 * - İK özlük dokümanları (HrDocument): expense-service talep yaşam döngüsünü
 *   yönetir, kod/imza/kanıt için aşağıdaki iç uçları çağırır.
 * - Çalışanın imzaladığı tüm belgeler: GET api/documents/signatures/mine.
 * Kanıt: belge türü + kimliği + sürümü, içerik SHA-256'sı, imzalayan, zaman,
 * yöntem, /24 IP; kanıt satırı değiştirilemez (tetikleyici), belge silinince
 * kanıt da silinir (belgenin saklama süresine bağlı).
 * "Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli)
 * elektronik imza değildir."
 * ==================================================================== */
[Route("api/documents/requests/{id:guid}")]
[Authorize]
public class DocumentSignatureController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly SignatureEngine _engine;
    public DocumentSignatureController(GovernanceDbContext db, SignatureEngine engine) { _db = db; _engine = engine; }

    /// <summary>Belge talebinin en son imza kanıtı.</summary>
    public static Task<SignatureEvidence?> EvidenceAsync(Sql sql, string tenant, Guid documentId, CancellationToken ct) =>
        SignatureEngine.LatestAsync(sql, tenant, Signatures.DocumentRequest, documentId, ct);

    public static object View(SignatureEvidence e, bool en) => SignatureEngine.View(e, en);

    private async Task<(Models.DocumentRequest? Doc, Person? Me, IActionResult? Error)> OwnDocumentAsync(Guid id, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var r = await _db.DocumentRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null || r.Status != "Issued" || r.DocumentEnc is null) return (null, me, NotFound(new { message = L("Belge bulunamadı.", "Document not found.") }));
        if (me?.Id != r.EmployeeId)
            return (null, me, StatusCode(403, new { message = L("Yalnızca belgenin sahibi imzalayabilir.", "Only the document's owner can sign it."), code = "not_owner" }));
        return (r, me, null);
    }

    private IActionResult Fail(SignatureError e) => StatusCode(e.Status, e.Body(En));

    public record OtpInput(string? Channel);

    /// <summary>İmza kodu ister. Önceki kullanılmamış kodlar geçersiz olur.</summary>
    [HttpPost("sign/otp")]
    public async Task<IActionResult> RequestOtp(OtpInput? body, CancellationToken ct)
    {
        var (r, me, err) = await OwnDocumentAsync(RouteId, ct);
        if (err is not null) return err;
        var channel = body?.Channel is "Email" ? "Email" : "InApp";
        var (otp, fail) = await _engine.RequestOtpAsync(Tenant, Signatures.DocumentRequest, r!.Id, me!.Id, r.TemplateName, channel, 1, ct);
        if (fail is not null) return Fail(fail);
        return Ok(new { otp!.OtpId, otp.Channel, otp.ExpiresAt, otp.MaxAttempts, otp.SendsLeft, disclaimer = En ? Signatures.DisclaimerEn : Signatures.DisclaimerTr });
    }

    public record SignInput(Guid? OtpId, string? Code);

    [HttpPost("sign")]
    public async Task<IActionResult> Sign(SignInput body, CancellationToken ct)
    {
        var (r, me, err) = await OwnDocumentAsync(RouteId, ct);
        if (err is not null) return err;
        var docHash = Signatures.Sha256Hex(SecretBox.Unprotect(r!.DocumentEnc)!);
        var ip = Request.Headers["X-Real-IP"].FirstOrDefault() ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString();
        var (e, fail) = await _engine.SignAsync(new SignRequest(Tenant, Signatures.DocumentRequest, r.Id, me!.Id, body.OtpId, body.Code, docHash, 1, ip,
            Me.UserId, Me.Name, r.TemplateName), ct);
        return fail is not null ? Fail(fail) : Ok(View(e!, En));
    }

    [HttpGet("signature")]
    public async Task<IActionResult> Signature(CancellationToken ct)
    {
        var r = await _db.DocumentRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == RouteId, ct);
        if (r is null) return NotFound();
        var me = await MyPersonAsync(ct);
        if (me?.Id != r.EmployeeId && !Me.IsHr) return NotFound();
        var e = await EvidenceAsync(Db, Tenant, r.Id, ct);
        return Ok(new { signed = e is not null, evidence = e is null ? null : View(e, En), disclaimer = En ? Signatures.DisclaimerEn : Signatures.DisclaimerTr });
    }

    private Guid RouteId => Guid.Parse((string)RouteData.Values["id"]!);
}

/// <summary>Çalışanın imzaladığı belgeler — tüm belge türleri (belge talepleri + İK özlük dokümanları).</summary>
[Route("api/documents/signatures")]
[Authorize]
public class MySignaturesController : AppController
{
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var disclaimer = En ? Signatures.DisclaimerEn : Signatures.DisclaimerTr;
        if (string.IsNullOrEmpty(HttpContext.RequestServices.GetRequiredService<Tenancy.ITenantContext>().TenantSlug))
            return Ok(new { items = Array.Empty<object>(), disclaimer });
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { items = Array.Empty<object>(), disclaimer });
        var rows = await SignatureEngine.MineAsync(Db, Tenant, me.Id, ct);
        return Ok(new { items = rows.Select(e => SignatureEngine.View(e, En)), disclaimer });
    }
}

/// <summary>
/// Servisler arası imza uçları (expense-service → HrDocument). INTERNAL_SERVICE_TOKEN ile korunur
/// (yoksa/yanlışsa 404); kiracı gövdeden alınır. Gateway /api/*/internal/ yollarını dışarıya kapatır.
/// Belgenin sahipliği ve içerik özeti çağıran serviste doğrulanır; DocumentRequest türü burada
/// imzalanamaz (sahiplik kontrolü yalnızca web uçlarında).
/// </summary>
[ApiController]
[Route("api/internal/signatures")]
[AllowAnonymous]
public class InternalSignaturesController : ControllerBase
{
    private readonly SignatureEngine _engine;
    private readonly Sql _sql;
    private readonly Tenancy.TenantContext _tenant;
    public InternalSignaturesController(SignatureEngine engine, Sql sql, Tenancy.TenantContext tenant) { _engine = engine; _sql = sql; _tenant = tenant; }

    private bool En => Request.Headers["X-HR360-Lang"].ToString().StartsWith("en", StringComparison.OrdinalIgnoreCase);
    private string L(string tr, string en) => En ? en : tr;

    public static bool TokenOk(string? given) => Security.InternalServiceToken.Matches(given);

    /// <summary>Anahtar + kiracı + belge türü denetimi; geçerse TenantContext gövdedeki kiracıya ayarlanır.</summary>
    private IActionResult? Guard(string? tenantSlug, string? documentType)
    {
        if (!TokenOk(Request.Headers["X-Internal-Token"].FirstOrDefault())) return NotFound();
        if (string.IsNullOrWhiteSpace(tenantSlug)) return BadRequest(new { message = L("Kiracı belirtilmedi.", "Tenant is missing."), code = "tenant_missing" });
        if (!Signatures.ValidDocumentType(documentType) || documentType == Signatures.DocumentRequest)
            return BadRequest(new { message = L("Geçersiz belge türü.", "Invalid document type."), code = "invalid_document_type" });
        _tenant.TenantSlug = tenantSlug.Trim();
        _tenant.IsPlatformAdmin = false;
        return null;
    }

    private IActionResult Fail(SignatureError e) => StatusCode(e.Status, e.Body(En));

    /// <summary>
    /// Dış imzalayan (dalga 11): DocumentType = OfferLetter iken SignerKind = Candidate ve SignerEmail zorunludur;
    /// EmployeeId alanı aday kimliğini taşır, kod yalnızca e-postayla gider (Lang: tr | en).
    /// </summary>
    public record OtpBody(string? TenantSlug, string? DocumentType, Guid DocumentId, Guid EmployeeId, string? Title, string? Channel, int? Version,
        string? SignerKind = null, string? SignerEmail = null, string? Lang = null);

    private IActionResult? SignerGuard(string documentType, string? kind, string? email, bool requireEmail) =>
        Signatures.SignerRule(documentType, kind, email, requireEmail) switch
        {
            null => null,
            "no_email" => BadRequest(new { message = L("İmzalayanın geçerli bir e-posta adresi yok.", "The signer has no valid email address."), code = "no_email" }),
            var code => BadRequest(new { message = L("Bu belge türü için imzalayan türü geçersiz.", "Invalid signer kind for this document type."), code }),
        };

    [HttpPost("otp")]
    public async Task<IActionResult> Otp([FromBody] OtpBody b, CancellationToken ct)
    {
        if (Guard(b.TenantSlug, b.DocumentType) is { } g) return g;
        if (b.DocumentId == Guid.Empty || b.EmployeeId == Guid.Empty) return BadRequest(new { message = L("Belge ve çalışan gerekli.", "Document and employee are required."), code = "invalid" });
        if (SignerGuard(b.DocumentType!, b.SignerKind, b.SignerEmail, requireEmail: true) is { } sg) return sg;
        var external = b.DocumentType == Signatures.OfferLetter ? b.SignerEmail!.Trim() : null;
        var (otp, fail) = await _engine.RequestOtpAsync(_tenant.TenantSlug!, b.DocumentType!, b.DocumentId, b.EmployeeId,
            string.IsNullOrWhiteSpace(b.Title) ? L("Belge", "Document") : b.Title.Trim(), b.Channel, Math.Max(1, b.Version ?? 1), ct,
            external, externalEn: (b.Lang ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (fail is not null) return Fail(fail);
        return Ok(new { otp!.OtpId, otp.Channel, otp.ExpiresAt, otp.MaxAttempts, otp.SendsLeft, disclaimer = En ? Signatures.DisclaimerEn : Signatures.DisclaimerTr });
    }

    public record SignBody(string? TenantSlug, string? DocumentType, Guid DocumentId, Guid EmployeeId, Guid? OtpId, string? Code, string? DocumentSha256,
        int? Version, string? Ip, string? UserId, string? UserName, string? Title, string? SignerKind = null);

    [HttpPost("sign")]
    public async Task<IActionResult> Sign([FromBody] SignBody b, CancellationToken ct)
    {
        if (Guard(b.TenantSlug, b.DocumentType) is { } g) return g;
        if (b.DocumentId == Guid.Empty || b.EmployeeId == Guid.Empty || b.DocumentSha256 is not { Length: 64 } h || !h.All(char.IsAsciiHexDigit))
            return BadRequest(new { message = L("Belge, çalışan ve belge özeti (SHA-256) gerekli.", "Document, employee and document hash (SHA-256) are required."), code = "invalid" });
        if (SignerGuard(b.DocumentType!, b.SignerKind, null, requireEmail: false) is { } sg) return sg;
        var kind = b.DocumentType == Signatures.OfferLetter ? Signatures.SignerCandidate : Signatures.SignerEmployee;
        var (e, fail) = await _engine.SignAsync(new SignRequest(_tenant.TenantSlug!, b.DocumentType!, b.DocumentId, b.EmployeeId, b.OtpId, b.Code,
            h.ToLowerInvariant(), Math.Max(1, b.Version ?? 1), b.Ip, b.UserId, b.UserName, b.Title, kind), ct);
        return fail is not null ? Fail(fail) : Ok(SignatureEngine.View(e!, En));
    }

    public record EvidenceBody(string? TenantSlug, string? DocumentType, Guid? DocumentId, Guid[]? Ids);

    /// <summary>Belgenin kanıtları (tür + kimlik) ve/veya kimliğiyle istenen kanıtlar; en yeni önce.</summary>
    [HttpPost("evidence")]
    public async Task<IActionResult> Evidence([FromBody] EvidenceBody b, CancellationToken ct)
    {
        if (Guard(b.TenantSlug, b.DocumentType) is { } g) return g;
        if (b.DocumentId is null && (b.Ids is null || b.Ids.Length == 0))
            return Ok(new { items = Array.Empty<object>(), disclaimer = En ? Signatures.DisclaimerEn : Signatures.DisclaimerTr });
        var rows = await SignatureEngine.ListAsync(_sql, _tenant.TenantSlug!, b.DocumentType!, b.DocumentId, b.Ids is { Length: > 0 } ? b.Ids : null, ct);
        return Ok(new { items = rows.Select(e => SignatureEngine.View(e, En)), disclaimer = En ? Signatures.DisclaimerEn : Signatures.DisclaimerTr });
    }
}
