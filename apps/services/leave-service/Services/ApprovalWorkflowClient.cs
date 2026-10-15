using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LeaveService.Services;

/// <summary>
/// Izin talebi olusturuldugunda otomatik bir onay akisi (WorkflowRequest)
/// baslatir - bu olmadan "Onay kutusu" sayfasi ve workflow.submitted
/// e-posta bildirimi hicbir zaman tetiklenmiyordu (talep sessizce
/// leave-service'in kendi "Submitted" durumunda beklemede kaliyordu).
///
/// Zincir: employee-service'ten calisanin aktif departmanini bul ->
/// organization-service'ten o departmanin basini (HeadEmployeeId) bul ->
/// workflow-service'te bir WorkflowRequest ac. Kimlik dogrulama, cagirani
/// yapan kullanicinin KENDI JWT'si iletilerek yapilir (pass-through) -
/// diger DirectoryClient'larla ayni desen.
///
/// Departman basi bulunamazsa (atanmamis ya da calisan kendisi bas ise) onayci
/// listesi bos gonderilir; workflow-service ust bolum basini, o da yoksa kiracinin
/// IK onaycisini atar (onceden akis hic acilmiyor, talep kimsenin kutusuna dusmeden
/// askida kaliyordu). Orada da kimse yoksa talep Submitted kalir ve mevcut
/// /resolve ucuyla (RequireManagerOrAbove) elle sonuclandirilabilir. Bu,
/// cagiran akisi (izin talebi olusturma) hicbir zaman engellememesi
/// gereken best-effort bir islem.
/// </summary>
public class ApprovalWorkflowClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _employeeServiceUrl;
    private readonly string _organizationServiceUrl;
    private readonly string _workflowServiceUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ApprovalWorkflowClient(HttpClient http, IHttpContextAccessor httpContextAccessor)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _employeeServiceUrl = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL")
            ?? "http://employee-service:8080").TrimEnd('/');
        _organizationServiceUrl = (Environment.GetEnvironmentVariable("ORGANIZATION_SERVICE_URL")
            ?? "http://organization-service:8080").TrimEnd('/');
        _workflowServiceUrl = (Environment.GetEnvironmentVariable("WORKFLOW_SERVICE_URL")
            ?? "http://workflow-service:8080").TrimEnd('/');
    }

    private HttpRequestMessage Build(HttpMethod method, string baseUrl, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{baseUrl}{path}");
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(incomingAuth))
            req.Headers.TryAddWithoutValidation("Authorization", incomingAuth);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return req;
    }

    private record AssignmentDto(Guid DepartmentId, DateOnly? EffectiveTo);
    private record EmployeeDto(Guid Id, List<AssignmentDto> Assignments);
    private record DepartmentDto(Guid Id, string Name, Guid? HeadEmployeeId);
    private record CreateWorkflowBody(
        int Type, Guid RequesterEmployeeId, string? Subject, string? Payload,
        List<Guid> ApproverEmployeeIds, int? SlaHours);
    private record WorkflowCreatedDto(Guid Id);

    /// <summary>
    /// Calisanin aktif departmaninin basini onayci yaparak workflow-service'te
    /// bir "LeaveRequest" (enum deger 0) turu WorkflowRequest acar ve
    /// olusan Id'yi doner. Zincirin herhangi bir adimi basarisiz olursa
    /// (departman yok, bas atanmamis, cross-service cagri hatasi) null doner.
    /// </summary>
    public async Task<Guid?> StartLeaveApprovalAsync(
        Guid employeeId, string? subject, CancellationToken ct, string? payload = null)
    {
        try
        {
            var empResp = await _http.SendAsync(
                Build(HttpMethod.Get, _employeeServiceUrl, $"/api/employees/{employeeId}"), ct);
            if (!empResp.IsSuccessStatusCode) return null;

            var employee = JsonSerializer.Deserialize<EmployeeDto>(
                await empResp.Content.ReadAsStringAsync(ct), JsonOpts);
            if (employee is null) return null;
            var activeDeptId = employee.Assignments
                .FirstOrDefault(a => a.EffectiveTo is null)?.DepartmentId;
            // Bos liste = onayciyi workflow-service belirler (ust bolum basi / IK onaycisi).
            var approvers = new List<Guid>();
            if (activeDeptId is not null)
            {
                var deptResp = await _http.SendAsync(
                    Build(HttpMethod.Get, _organizationServiceUrl, $"/api/departments/{activeDeptId}"), ct);
                if (!deptResp.IsSuccessStatusCode) return null;

                var dept = JsonSerializer.Deserialize<DepartmentDto>(
                    await deptResp.Content.ReadAsStringAsync(ct), JsonOpts);
                if (dept?.HeadEmployeeId is { } headId && headId != employeeId) approvers.Add(headId);
            }

            var wfResp = await _http.SendAsync(
                Build(HttpMethod.Post, _workflowServiceUrl, "/api/workflows", new CreateWorkflowBody(
                    Type: 0, // WorkflowType.LeaveRequest
                    RequesterEmployeeId: employeeId,
                    Subject: subject,
                    Payload: payload,
                    ApproverEmployeeIds: approvers,
                    SlaHours: null)), ct);
            if (!wfResp.IsSuccessStatusCode) return null;

            var created = JsonSerializer.Deserialize<WorkflowCreatedDto>(
                await wfResp.Content.ReadAsStringAsync(ct), JsonOpts);
            return created?.Id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Servisler arasi (jetonsuz) yol: departman basi ayni veritabanindaki tablolardan
    /// (kiraci filtresiyle) bulunur, akis workflow-service'in ic ucuyla acilir.
    /// </summary>
    public async Task<Guid?> StartLeaveApprovalInternalAsync(Data.LeaveDbContext db, Guid employeeId, string? subject, CancellationToken ct, string? payload = null)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        var tenant = db.CurrentTenantSlug;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(tenant)) return null;
        try
        {
            var head = await db.Database.SqlQueryRaw<Guid?>(
                """
                SELECT d."HeadEmployeeId" AS "Value" FROM employee_assignments a
                JOIN organization_departments d ON d."Id" = a."DepartmentId"
                WHERE a."TenantSlug" = {0} AND a."EmployeeId" = {1} AND a."EffectiveTo" IS NULL
                ORDER BY a."EffectiveFrom" DESC LIMIT 1
                """, tenant, employeeId).FirstOrDefaultAsync(ct);
            // Bas yoksa (ya da calisan kendisi bas ise) bos liste: workflow-service ust bolum
            // basini, o da yoksa IK onaycisini atar.
            var approvers = head is { } h && h != employeeId ? new[] { h } : Array.Empty<Guid>();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_workflowServiceUrl}/api/internal/workflows")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tenantSlug = tenant, type = 0, requesterEmployeeId = employeeId, subject, payload,
                    approverEmployeeIds = approvers, slaHours = (int?)null,
                }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Internal-Token", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var created = JsonSerializer.Deserialize<WorkflowCreatedDto>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            return created?.Id;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Talep sahibinin iptal ettigi kaydin onay akisini kapatir (cagiranin jetonuyla).
    /// Basarisiz olursa false - kayit yine iptal edilir; akisin karari zaten
    /// yok sayilir (tuketici yalnizca Submitted kayitlara uygular).
    /// </summary>
    public async Task<bool> CancelWorkflowAsync(Guid workflowId, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(
                Build(HttpMethod.Post, _workflowServiceUrl, $"/api/workflows/{workflowId}/cancel"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Servisler arasi (jetonsuz) yol: talep sahibi adina akisi workflow-service'in ic ucuyla
    /// kapatir (sohbet botundan iptal). Basarisiz olursa false - kayit yine iptal edilir.
    /// </summary>
    public async Task<bool> CancelWorkflowInternalAsync(string tenantSlug, Guid workflowId, Guid requesterEmployeeId, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(tenantSlug)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_workflowServiceUrl}/api/internal/workflows/{workflowId}/cancel")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { tenantSlug, actorEmployeeId = requesterEmployeeId }),
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Internal-Token", token);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Istegi yapan kullanicinin kendi calisan kaydinin ID'sini bulur -
    /// "kendi actigi talebi kendi sonuclandiramaz" (self-approval engeli)
    /// ve "employee rolu yalnizca kendi kaydini sorgulayabilir" (GetAll
    /// IDOR duzeltmesi) kontrollerinde kullanilir.
    ///
    /// employee-service'teki /api/employees/me ucunu kullanir (JWT'nin
    /// KeycloakUserId'siyle eslesen kaydi doner). Onceki surum JWT email'ini
    /// employee.Email ile eslestiriyordu - ama bir kullanicinin Keycloak
    /// giris e-postasi ile employee kaydindaki e-postasi FARKLI olabiliyor
    /// (orn. sirket kaydi kisisel e-postayla acilmis, Keycloak girisi
    /// kurumsal e-postayla); bu durumda email sorgusu sessizce bos donuyor,
    /// self-approval kontrolu de sessizce atlaniyordu (bugun canli ortamda
    /// dogrulandi). /me, KeycloakUserId uzerinden eslestigi icin bu sorunu
    /// tasimiyor.
    /// </summary>
    public async Task<Guid?> FindMyEmployeeIdAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.SendAsync(
                Build(HttpMethod.Get, _employeeServiceUrl, "/api/employees/me"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var me = JsonSerializer.Deserialize<EmployeeIdDto>(
                await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            return me?.Id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private record EmployeeIdDto(Guid Id);
}
