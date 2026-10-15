using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;

namespace TimeShiftService.Services;

/// <summary>
/// Onay akışı başlatma (fazla mesai): çalışanın aktif departmanının başı onaycı olur. Akış
/// workflow-service'in iç ucuyla açılır (INTERNAL_SERVICE_TOKEN); bölüm başı aynı veritabanından
/// kiracı filtresiyle bulunur. Başarısız olursa null döner, talep "Beklemede" kalır ve İK
/// panelden karar verebilir.
/// </summary>
public class ApprovalStarter
{
    private readonly HttpClient _http;
    private readonly ILogger<ApprovalStarter> _log;
    private readonly string _workflowUrl = (Environment.GetEnvironmentVariable("WORKFLOW_SERVICE_URL") ?? "http://workflow-service:8080").TrimEnd('/');

    public ApprovalStarter(HttpClient http, ILogger<ApprovalStarter> log) { _http = http; _log = log; }

    public static Task<Guid?> DepartmentHeadAsync(TimeShiftDbContext db, string tenant, Guid employeeId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<Guid?>(
            """
            SELECT d."HeadEmployeeId" AS "Value" FROM employee_assignments a
            JOIN organization_departments d ON d."Id" = a."DepartmentId"
            WHERE a."TenantSlug" = {0} AND a."EmployeeId" = {1} AND a."EffectiveTo" IS NULL
            ORDER BY a."EffectiveFrom" DESC LIMIT 1
            """, tenant, employeeId).FirstOrDefaultAsync(ct);

    /// <param name="type">workflow-service WorkflowType sayısal değeri (Overtime = 5).</param>
    public async Task<Guid?> StartAsync(TimeShiftDbContext db, Guid employeeId, int type, string subject, string? payload, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        var tenant = db.CurrentTenantSlug;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(tenant)) return null;
        try
        {
            var head = await DepartmentHeadAsync(db, tenant, employeeId, ct);
            if (head is null || head == employeeId) return null;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_workflowUrl}/api/internal/workflows")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tenantSlug = tenant, type, requesterEmployeeId = employeeId, subject, payload,
                    approverEmployeeIds = new[] { head.Value }, slaHours = (int?)null,
                }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Internal-Token", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Onay akışı açılamadı: {Status}", resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetGuid() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Onay akışı açılamadı");
            return null;
        }
    }

    /// <summary>
    /// Talep iptal edilince onay akışını workflow-service'in iç ucuyla kapatır (talep sahibi adına).
    /// Başarısızsa false — kayıt yine iptal edilmiş kalır.
    /// </summary>
    public async Task<bool> CancelAsync(string tenant, Guid workflowId, Guid requesterEmployeeId, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(tenant)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_workflowUrl}/api/internal/workflows/{workflowId}/cancel")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { tenantSlug = tenant, actorEmployeeId = requesterEmployeeId }),
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Internal-Token", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) _log.LogWarning("Onay akışı kapatılamadı: {Status}", resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Onay akışı kapatılamadı");
            return false;
        }
    }
}
