using System.Net.Http.Headers;
using System.Text.Json;

namespace NotificationService.Services;

/// <summary>
/// employee-service'ten calisanin e-posta adresini ceker.
///
/// NEDEN GEREKLI: NotificationsController.Create (POST /api/notifications)
/// yalnizca RecipientEmployeeId aliyordu, RecipientEmail'i HICBIR ZAMAN
/// doldurmuyordu - bu ucla olusturulan HER Email kanalli bildirim,
/// EmailSenderWorker'a ulastiginda "Alici e-posta adresi yok" ile
/// KESIN olarak basarisiz oluyordu (3 denemeden sonra Failed), RecipientEmployeeId
/// gecerli ve calisanin e-postasi bilinir olsa bile (hardcore test sirasinda
/// bulundu - frontend'in notificationApi.create'i "Email" kanalini secenek
/// olarak sunuyor, ama hicbir zaman calismazdi). Kafka event'lerinden gelen
/// bildirimler (HrEventConsumer) e-postayi event payload'undan zaten
/// dolduruyordu, bu yuzden sorun sadece dogrudan REST API'den olusturulan
/// bildirimlerde gorunuyordu.
///
/// Cagiranin JWT'si ileri tasinir (performance-service/DirectoryClient ile
/// ayni desen) - yetki kontrolu employee-service'te zaten yapiliyor.
/// Cozulemezse (calisan bulunamadi, servis erisilemez) null doner - bu
/// bildirim olusturmayi ENGELLEMEMELI; RecipientEmail bos kalirsa
/// EmailSenderWorker zaten bunu acik bir "Alici e-posta adresi yok"
/// hatasiyla Failed'e dusurup ayni onceki (guvenli) davranisi korur.
/// </summary>
public class EmployeeDirectoryClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _ctx;
    private readonly ILogger<EmployeeDirectoryClient> _logger;
    private readonly string _employeeBase;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public EmployeeDirectoryClient(HttpClient http, IHttpContextAccessor ctx, ILogger<EmployeeDirectoryClient> logger)
    {
        _http = http;
        _ctx = ctx;
        _logger = logger;
        _employeeBase = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL")
            ?? "http://employee-service:8080").TrimEnd('/');
    }

    public async Task<string?> GetEmailAsync(Guid employeeId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_employeeBase}/api/employees/{employeeId}");
            var auth = _ctx.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth[7..]);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<EmployeeEmailDto>(body, Json);
            return string.IsNullOrWhiteSpace(dto?.Email) ? null : dto.Email;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calisan e-postasi cozulemedi: {EmployeeId}", employeeId);
            return null;
        }
    }

    /// <summary>
    /// Cagiran kullanicinin kendi Employee.Id'si (employee-service /me,
    /// KeycloakUserId eslesmesi). Bulunamazsa/erisilemezse null - cagiranlar
    /// bunu FAIL-CLOSED yorumlamali (bkz. NotificationsController).
    /// </summary>
    public async Task<Guid?> FindMyEmployeeIdAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_employeeBase}/api/employees/me");
            var auth = _ctx.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth[7..]);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var dto = JsonSerializer.Deserialize<EmployeeIdDto>(await resp.Content.ReadAsStringAsync(ct), Json);
            return dto?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cagiranin calisan kaydi cozulemedi (/me)");
            return null;
        }
    }

    private record EmployeeEmailDto(string? Email);
    private record EmployeeIdDto(Guid Id);
}
