using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TenantService.Services;

/// <summary>
/// employee-service'e cross-service HTTP cagrilari yapar. Kimlik dogrulama
/// SERVICE-TO-SERVICE ozel bir token ILE DEGIL, cagirani yapan kullanicinin
/// KENDI JWT'si employee-service'e AKTARILARAK yapilir (pass-through) -
/// boylece employee-service kendi RequireHrAdmin/RequireTenantAdmin
/// policy'lerini normal sekilde uygular, iki servis arasinda AYRI bir
/// guven iliskisi kurmaya gerek kalmaz.
/// </summary>
public class EmployeeDirectoryClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public EmployeeDirectoryClient(HttpClient http, IHttpContextAccessor httpContextAccessor)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _baseUrl = (Environment.GetEnvironmentVariable("EMPLOYEE_SERVICE_URL")
            ?? "http://employee-service:8080").TrimEnd('/');
    }

    private HttpRequestMessage Build(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");

        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        // GUVENLIK (Dalga 5d): SCIM istegindeki kiraci SCIM jetonu baska servislere ASLA
        // iletilmez - yalnizca kullanicinin Keycloak JWT'si aktarilir.
        if (!string.IsNullOrEmpty(incomingAuth)
            && !incomingAuth.StartsWith("Bearer " + TenantService.Directory.ScimTokenService.Prefix, StringComparison.Ordinal))
            req.Headers.TryAddWithoutValidation("Authorization", incomingAuth);

        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

        return req;
    }

    public record EmployeeSummary(Guid Id, string FirstName, string LastName, string Email, string? KeycloakUserId);

    public async Task<List<EmployeeSummary>> GetAllAsync(CancellationToken ct)
    {
        var resp = await _http.SendAsync(Build(HttpMethod.Get, "/api/employees"), ct);
        if (!resp.IsSuccessStatusCode) return new List<EmployeeSummary>();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<EmployeeSummary>>(json, JsonOpts) ?? new List<EmployeeSummary>();
    }

    public async Task<EmployeeSummary?> GetByIdAsync(Guid employeeId, CancellationToken ct)
    {
        var resp = await _http.SendAsync(Build(HttpMethod.Get, $"/api/employees/{employeeId}"), ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<EmployeeSummary>(json, JsonOpts);
    }

    public sealed record DirectoryUpsertResult(Guid? EmployeeId, bool Created, string? Error);

    /// <summary>
    /// Dalga 5d (Y26): dizin (SCIM/LDAP) saglamasinda calisan kaydini olusturur/gunceller.
    /// Kullanici jetonu OLMADIGI icin (SCIM istemcisi, arka plan esitlemesi) employee-service'in
    /// servisler arasi ucuna X-Internal-Token (INTERNAL_SERVICE_TOKEN) ile gider; Authorization
    /// basligi (SCIM jetonu dahil) ASLA iletilmez. KVKK: yalnizca ad, soyad, e-posta, unvan,
    /// departman kimligi gonderilir.
    /// </summary>
    public async Task<DirectoryUpsertResult> UpsertDirectoryEmployeeAsync(
        string tenantSlug, string keycloakUserId, string email, string firstName, string lastName,
        string? positionTitle, Guid? departmentId, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token))
            return new(null, false, "Sunucuda INTERNAL_SERVICE_TOKEN tanımlı değil; çalışan kaydı oluşturulamadı");
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/internal/employees/directory-upsert")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                tenantSlug, keycloakUserId, email, firstName, lastName, positionTitle, departmentId,
            }, JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Internal-Token", token);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string? msg = null;
                try { msg = JsonDocument.Parse(text).RootElement.GetProperty("message").GetString(); } catch { }
                return new(null, false, msg ?? $"Çalışan kaydı oluşturulamadı ({(int)resp.StatusCode})");
            }
            using var doc = JsonDocument.Parse(text);
            return new(doc.RootElement.GetProperty("employeeId").GetGuid(),
                doc.RootElement.TryGetProperty("created", out var c) && c.GetBoolean(), null);
        }
        catch (HttpRequestException) { return new(null, false, "Çalışan servisine ulaşılamadı"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(null, false, "Çalışan servisi zaman aşımı"); }
    }

    public async Task LinkKeycloakUserAsync(Guid employeeId, string keycloakUserId, CancellationToken ct)
    {
        var resp = await _http.SendAsync(
            Build(HttpMethod.Put, $"/api/employees/{employeeId}/keycloak-link", new { keycloakUserId }), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Calisan-Keycloak baglantisi kurulamadi ({(int)resp.StatusCode}): {err}");
        }
    }
}
