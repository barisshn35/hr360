using System.Text;
using System.Text.Json;

namespace RecruitmentService.Services;

/// <summary>
/// Y18: teklif onayı için workflow-service'te "OfferApproval" (enum değeri 8) türünde akış açar.
/// Kimlik doğrulama, isteği yapan İK kullanıcısının KENDİ jetonuyla yapılır (expense-service
/// ApprovalWorkflowClient deseni). Onaycı, ilanın departman başıdır (işe alım yöneticisi).
/// </summary>
public class ApprovalWorkflowClient
{
    public const int OfferApprovalType = 8;

    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _ctx;
    private readonly string _workflowUrl;
    private readonly ILogger<ApprovalWorkflowClient> _log;

    public ApprovalWorkflowClient(HttpClient http, IHttpContextAccessor ctx, ILogger<ApprovalWorkflowClient> log)
    {
        _http = http;
        _ctx = ctx;
        _log = log;
        _workflowUrl = (Environment.GetEnvironmentVariable("WORKFLOW_SERVICE_URL") ?? "http://workflow-service:8080").TrimEnd('/');
    }

    private record CreateWorkflowBody(int Type, Guid RequesterEmployeeId, string? Subject, string? Payload, List<Guid> ApproverEmployeeIds, int? SlaHours);
    private record Created(Guid Id);

    /// <summary>Akış kimliğini ya da (hata durumunda) null ile hata metnini döner.</summary>
    public async Task<(Guid? Id, string? Error)> StartAsync(Guid requesterEmployeeId, Guid approverEmployeeId, string subject, string payload, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_workflowUrl}/api/workflows")
            {
                Content = new StringContent(JsonSerializer.Serialize(new CreateWorkflowBody(
                    OfferApprovalType, requesterEmployeeId, subject, payload, new List<Guid> { approverEmployeeId }, 72)), Encoding.UTF8, "application/json"),
            };
            var auth = _ctx.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
            var res = await _http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Teklif onay akışı açılamadı: {Status} {Body}", (int)res.StatusCode, text.Length > 300 ? text[..300] : text);
                return (null, $"Onay akışı başlatılamadı (HTTP {(int)res.StatusCode})");
            }
            var created = JsonSerializer.Deserialize<Created>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (created?.Id, created is null ? "Onay akışı yanıtı okunamadı" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "workflow-service erişilemedi");
            return (null, "Onay servisine ulaşılamadı");
        }
    }
}
