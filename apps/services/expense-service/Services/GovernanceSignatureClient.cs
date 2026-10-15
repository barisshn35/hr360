using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseService.Models;

namespace ExpenseService.Services;

/// <summary>governance_signatures kanit satiri (governance iç ucundan).</summary>
public sealed record GovEvidence(
    Guid Id, string DocumentType, Guid DocumentId, int DocumentVersion, string DocumentSha256, Guid SignerEmployeeId,
    DateTimeOffset SignedAt, string Method, string? IpPrefix, string? Title, string Disclaimer, string EvidenceSha256, bool IntegrityOk);

public sealed record GovOtp(Guid OtpId, string Channel, DateTimeOffset ExpiresAt, int MaxAttempts, int SendsLeft, string? Disclaimer);

/// <summary>Governance cagrisi sonucu: basarida Value, hatada durum + {message, code, attemptsLeft}.</summary>
public sealed record GovResult<T>(int Status, T? Value, string? Message, string? Code, int? AttemptsLeft)
{
    public bool Ok => Value is not null && Status is >= 200 and < 300;
}

/// <summary>
/// Y28: OTP + imza + kanit governance-service'teki TEK imza motoruna devredilir
/// (POST /api/internal/signatures/{otp|sign|evidence}, X-Internal-Token). expense yalnizca
/// talep yasam dongusunu (IK gonderir/iptal eder, calisan listeler) tutar.
/// </summary>
public class GovernanceSignatureClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _ctx;
    private readonly ILogger<GovernanceSignatureClient> _log;
    private readonly string _baseUrl;

    public GovernanceSignatureClient(HttpClient http, IHttpContextAccessor ctx, ILogger<GovernanceSignatureClient> log)
    {
        _http = http;
        _ctx = ctx;
        _log = log;
        _baseUrl = (Environment.GetEnvironmentVariable("GOVERNANCE_SERVICE_URL") ?? "http://governance-service:8080").TrimEnd('/');
    }

    public const string Unavailable = "signature_service_unavailable";

    private async Task<GovResult<T>> PostAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/internal/signatures/{path}")
            {
                Content = JsonContent.Create(body, options: Json),
            };
            req.Headers.TryAddWithoutValidation("X-Internal-Token", Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN") ?? "");
            var lang = _ctx.HttpContext?.Request.Headers["X-HR360-Lang"].ToString();
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
            _log.LogWarning("Imza motoruna ulasilamadi ({Path}): {Message}", path, ex.Message);
            return new GovResult<T>(503, null, "İmza servisine ulaşılamadı; lütfen biraz sonra tekrar deneyin.", Unavailable, null);
        }
    }

    /// <summary>Governance hata govdesi {message, code, attemptsLeft?}; 404 govdesizse ic uc kapali (anahtar) demektir.</summary>
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

    public Task<GovResult<GovOtp>> RequestOtpAsync(string tenant, Guid documentId, Guid employeeId, string title, string channel, int version, CancellationToken ct) =>
        PostAsync<GovOtp>("otp", new
        {
            tenantSlug = tenant, documentType = SimpleSignature.DocumentType, documentId, employeeId, title, channel, version,
        }, ct);

    public Task<GovResult<GovEvidence>> SignAsync(string tenant, Guid documentId, Guid employeeId, Guid? otpId, string? code, string documentSha256,
        int version, string? ip, string? userId, string? userName, string title, CancellationToken ct) =>
        PostAsync<GovEvidence>("sign", new
        {
            tenantSlug = tenant, documentType = SimpleSignature.DocumentType, documentId, employeeId, otpId, code, documentSha256, version, ip,
            userId, userName, title,
        }, ct);

    private sealed record EvidenceList(List<GovEvidence> Items);

    /// <summary>Kanitlar: dokumanin tum kanitlari (documentId) ya da kimlikle istenenler (ids). Hata/erisim yoksa bos.</summary>
    public async Task<Dictionary<Guid, GovEvidence>> EvidenceAsync(string tenant, Guid? documentId, IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        if (documentId is null && (ids is null || ids.Count == 0)) return new();
        var r = await PostAsync<EvidenceList>("evidence", new
        {
            tenantSlug = tenant, documentType = SimpleSignature.DocumentType, documentId, ids = ids?.ToArray(),
        }, ct);
        return r.Value?.Items.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First()) ?? new();
    }
}

/// <summary>Governance kanitinin expense API gorunumune (EvidenceView alanlari) eslenmesi — saf, birim testli.</summary>
public static class SignatureViews
{
    /// <summary>"OTP-InApp+Email" → "InApp+Email" (eski otpChannel alani).</summary>
    public static string ChannelFromMethod(string method) =>
        method.StartsWith("OTP-", StringComparison.Ordinal) ? method[4..] : method;

    public static object Evidence(Guid signatureId, GovEvidence e) => new
    {
        e.Id, signatureId, e.DocumentId, e.SignerEmployeeId, e.SignedAt, documentHash = e.DocumentSha256, ipMasked = e.IpPrefix,
        userAgentHash = (string?)null, otpChannel = ChannelFromMethod(e.Method), e.Method, evidenceHash = e.EvidenceSha256, e.IntegrityOk,
        e.DocumentVersion, source = "governance",
    };

    public static object Legacy(SignatureEvidence e) => new
    {
        e.Id, e.SignatureId, e.DocumentId, e.SignerEmployeeId, e.SignedAt, documentHash = e.DocumentHash, e.IpMasked,
        e.UserAgentHash, e.OtpChannel, e.Method, e.EvidenceHash,
        // Butunluk: kayitli alanlardan yeniden hesaplanan ozet saklanan ozetle ayni mi.
        integrityOk = SimpleSignature.EvidenceHash(e) == e.EvidenceHash,
        documentVersion = 1, source = "legacy",
    };
}
