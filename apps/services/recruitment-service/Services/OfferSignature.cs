using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecruitmentService.Models;

namespace RecruitmentService.Services;

/// <summary>
/// Dalga 11: teklif mektubunun aday tarafından basit elektronik imzayla (OTP) kabulü — saf kurallar.
/// İmza, kod ve kanıt governance-service'teki TEK imza motorundadır (DocumentType "OfferLetter",
/// SignerKind "Candidate"); recruitment yalnızca imzalı belgeyi (HTML) ve özetlerini saklar.
/// </summary>
public static class OfferSignature
{
    public const string DocumentType = "OfferLetter";
    public const string SignerKind = "Candidate";

    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>İmzalanan içerik: mektup metni (satır sonları normalize). Aday ekranda bu metni görür.</summary>
    public static string LetterHash(string letterText) => Sha256Hex(letterText.Replace("\r\n", "\n"));

    /// <summary>Kanıt başlığı: aday adı/ücret içermez (governance kayıtları ve olaylarına gider).</summary>
    public static string Title(Offer o) => $"İş teklifi: {o.PositionTitle}";

    /// <summary>Teklif imzalanabilir mi? Null: evet; değilse adaya gösterilecek ileti.</summary>
    public static string? CanSign(Offer o, DateOnly today) =>
        o.SignatureEvidenceId is not null ? "Teklif zaten imzalanmış"
        : o.Status != OfferStatus.Sent ? "Bu teklif imzaya açık değil"
        : o.ExpiresAt < today ? "Teklifin geçerlilik süresi dolmuş"
        : null;

    /// <summary>E-posta adresini maskeler (a***@alan.com).</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return "";
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[0] + new string('*', Math.Max(2, at - 1)) + email[at..];
    }

    /// <summary>
    /// İmzalı belge: mektup metni + kanıt bloğu (HTML; tüm değerler kodlanır, betik yok). Belgenin
    /// SHA-256'sı ayrıca saklanır; kanıttaki belge özeti mektup metninin özetidir.
    /// </summary>
    public static string RenderSignedHtml(string company, Offer o, string letterHash, Guid evidenceId, string evidenceSha256,
        DateTimeOffset signedAt, string method, string disclaimer)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var local = signedAt.ToOffset(TimeSpan.FromHours(3)); // Türkiye saati (sabit UTC+3)
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\"><title>").Append(E(Title(o))).Append("</title>")
          .Append("<style>body{font-family:system-ui,sans-serif;max-width:760px;margin:32px auto;padding:0 16px;color:#111}")
          .Append("pre{white-space:pre-wrap;font-family:inherit;line-height:1.55}")
          .Append(".ev{margin-top:32px;border-top:2px solid #333;padding-top:12px;font-size:12px}.ev td{padding:2px 8px 2px 0;vertical-align:top}")
          .Append(".mono{font-family:ui-monospace,monospace;word-break:break-all}</style></head><body>")
          .Append("<h1 style=\"font-size:18px\">").Append(E(company)).Append(" — ").Append(E(Title(o))).Append("</h1>")
          .Append("<pre>").Append(E(o.SalaryLetterText.Replace("\r\n", "\n"))).Append("</pre>")
          .Append("<div class=\"ev\"><strong>Elektronik imza kanıtı</strong><table>")
          .Append("<tr><td>Durum</td><td>Aday tarafından tek kullanımlık e-posta koduyla imzalandı ve kabul edildi</td></tr>")
          .Append("<tr><td>İmza zamanı</td><td>").Append(E(local.ToString("dd.MM.yyyy HH:mm:ss"))).Append(" (Türkiye saati)</td></tr>")
          .Append("<tr><td>Yöntem</td><td>").Append(E(method)).Append("</td></tr>")
          .Append("<tr><td>Belge özeti (SHA-256)</td><td class=\"mono\">").Append(E(letterHash)).Append("</td></tr>")
          .Append("<tr><td>Kanıt kimliği</td><td class=\"mono\">").Append(evidenceId).Append("</td></tr>")
          .Append("<tr><td>Kanıt özeti</td><td class=\"mono\">").Append(E(evidenceSha256)).Append("</td></tr>")
          .Append("</table><p>").Append(E(disclaimer)).Append("</p></div></body></html>");
        return sb.ToString();
    }
}

/// <summary>governance_signatures kanıt satırı (governance iç ucundan).</summary>
public sealed record GovEvidence(
    Guid Id, string DocumentType, Guid DocumentId, int DocumentVersion, string DocumentSha256, Guid SignerEmployeeId,
    DateTimeOffset SignedAt, string Method, string? IpPrefix, string? Title, string Disclaimer, string EvidenceSha256, bool IntegrityOk);

public sealed record GovOtp(Guid OtpId, string Channel, DateTimeOffset ExpiresAt, int MaxAttempts, int SendsLeft, string? Disclaimer);

/// <summary>Governance çağrısı sonucu: başarıda Value, hatada durum + {message, code, attemptsLeft}.</summary>
public sealed record GovResult<T>(int Status, T? Value, string? Message, string? Code, int? AttemptsLeft)
{
    public bool Ok => Value is not null && Status is >= 200 and < 300;
}

/// <summary>
/// Dalga 11: OTP + imza + kanıt governance-service'teki TEK imza motoruna devredilir
/// (POST /api/internal/signatures/{otp|sign|evidence}, X-Internal-Token) — expense-service ile aynı desen.
/// </summary>
public class GovernanceSignatureClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly HttpClient _http;
    private readonly ILogger<GovernanceSignatureClient> _log;
    private readonly string _baseUrl;

    public GovernanceSignatureClient(HttpClient http, ILogger<GovernanceSignatureClient> log)
    {
        _http = http;
        _log = log;
        _baseUrl = (Environment.GetEnvironmentVariable("GOVERNANCE_SERVICE_URL") ?? "http://governance-service:8080").TrimEnd('/');
    }

    public const string Unavailable = "signature_service_unavailable";

    private async Task<GovResult<T>> PostAsync<T>(string path, object body, string? lang, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/internal/signatures/{path}")
            {
                Content = JsonContent.Create(body, options: Json),
            };
            req.Headers.TryAddWithoutValidation("X-Internal-Token", Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN") ?? "");
            if (!string.IsNullOrEmpty(lang)) req.Headers.TryAddWithoutValidation("X-HR360-Lang", lang);
            using var res = await _http.SendAsync(req, ct);
            var status = (int)res.StatusCode;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
                return new GovResult<T>(status, JsonSerializer.Deserialize<T>(text, Json), null, null, null);
            return ParseError<T>(status, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            _log.LogWarning("İmza motoruna ulaşılamadı ({Path}): {Message}", path, ex.Message);
            return new GovResult<T>(503, null, "İmza servisine ulaşılamadı; lütfen biraz sonra tekrar deneyin.", Unavailable, null);
        }
    }

    /// <summary>Governance hata gövdesi {message, code, attemptsLeft?}; gövdesiz 404 iç ucun kapalı (anahtar) olduğunu gösterir.</summary>
    public static GovResult<T> ParseError<T>(int status, string? text)
    {
        string? message = null, code = null;
        int? left = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                    if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                    if (root.TryGetProperty("attemptsLeft", out var a) && a.ValueKind == JsonValueKind.Number) left = a.GetInt32();
                }
            }
        }
        catch (JsonException) { }
        if (code is null)
            return new GovResult<T>(503, default, "İmza servisine ulaşılamadı; lütfen biraz sonra tekrar deneyin.", Unavailable, null);
        return new GovResult<T>(status, default, message, code, left);
    }

    public Task<GovResult<GovOtp>> RequestOtpAsync(string tenant, Guid offerId, Guid candidateId, string email, string title, string? lang, CancellationToken ct) =>
        PostAsync<GovOtp>("otp", new
        {
            tenantSlug = tenant, documentType = OfferSignature.DocumentType, documentId = offerId, employeeId = candidateId, title,
            channel = "Email", version = 1, signerKind = OfferSignature.SignerKind, signerEmail = email, lang,
        }, lang, ct);

    public Task<GovResult<GovEvidence>> SignAsync(string tenant, Guid offerId, Guid candidateId, Guid? otpId, string? code, string documentSha256,
        string title, string? lang, CancellationToken ct) =>
        PostAsync<GovEvidence>("sign", new
        {
            // KVKK: herkese açık uçlarda IP saklanmaz (kariyer sayfasıyla aynı ilke) — ip gönderilmez.
            tenantSlug = tenant, documentType = OfferSignature.DocumentType, documentId = offerId, employeeId = candidateId, otpId, code,
            documentSha256, version = 1, ip = (string?)null, userId = "candidate", userName = "candidate", title, signerKind = OfferSignature.SignerKind,
        }, lang, ct);

    private sealed record EvidenceList(List<GovEvidence> Items);

    /// <summary>Teklifin kanıtları (en yeni önce). Hata/erişim yoksa boş.</summary>
    public async Task<List<GovEvidence>> EvidenceAsync(string tenant, Guid offerId, CancellationToken ct)
    {
        var r = await PostAsync<EvidenceList>("evidence", new { tenantSlug = tenant, documentType = OfferSignature.DocumentType, documentId = offerId, ids = (Guid[]?)null }, null, ct);
        return r.Value?.Items ?? new();
    }
}
