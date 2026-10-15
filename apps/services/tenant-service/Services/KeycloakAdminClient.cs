using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TenantService.Services;

/// <summary>
/// Keycloak Admin REST API sarmalayicisi. Organization olusturma, kullanici
/// saglama ve rol atama islerini yapar.
///
/// Kimlik dogrulama (Guvenlik dalgasi 2A): hr360 realm'indeki ayri, gizli
/// "hr360-tenant-admin" istemcisinin servis hesabiyla (client_credentials).
/// Hesap yalnizca realm-management'in gerekli rollerine sahiptir (manage-users,
/// manage-realm, manage-identity-providers, manage-clients, view-events) ve master
/// realm'e hic erisemez. Gizli anahtar: KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET
/// (scripts/keycloak-service-account.sh uretir ve Keycloak'a tanimlar).
///
/// Eski kurulumlar icin geri donus: anahtar tanimli degilse (ya da Keycloak istemciyi
/// henuz tanimiyorsa) master realm yonetici hesabiyla parola girisi yapilir ve bir kez
/// uyari yazilir. Bu yol, master realm'de kaba kuvvet korumasi acikken hesabi
/// kilitleyebildigi icin yalnizca gecis icindir.
///
/// Jeton surec genelinde tek (statik onbellek + SemaphoreSlim): HttpClient ile kayitli
/// bu sinif istek basina yeniden olusturulur; onceden her istek ayri ve eszamanli
/// giris yapiyordu.
/// </summary>
public class KeycloakAdminClient
{
    private readonly HttpClient _http;
    private readonly ILogger<KeycloakAdminClient> _logger;
    private readonly string _baseUrl;
    private readonly string _realm;
    private readonly string? _adminUser;
    private readonly string? _adminPassword;
    private readonly string _clientId;
    private readonly string? _clientSecret;

    // Surec genelinde paylasilan jeton (bkz. sinif aciklamasi).
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
    private static volatile CachedToken? _cached;
    private static int _legacyWarned;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public KeycloakAdminClient(HttpClient http, ILogger<KeycloakAdminClient> logger)
    {
        _http = http;
        _logger = logger;
        _baseUrl = (Environment.GetEnvironmentVariable("KEYCLOAK_BASE_URL")
            ?? "http://keycloak:8080/auth").TrimEnd('/');
        _realm = Environment.GetEnvironmentVariable("KEYCLOAK_REALM") ?? "hr360";
        _clientId = Environment.GetEnvironmentVariable("KEYCLOAK_TENANT_ADMIN_CLIENT_ID") is { Length: > 0 } cid
            ? cid : "hr360-tenant-admin";
        _clientSecret = Environment.GetEnvironmentVariable("KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET") is { Length: > 0 } sec
            ? sec : null;
        _adminUser = Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_USER");
        _adminPassword = Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_PASSWORD");
        if (_clientSecret is null && (string.IsNullOrEmpty(_adminUser) || string.IsNullOrEmpty(_adminPassword)))
            throw new InvalidOperationException(
                "KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET (ya da eski yol icin KEYCLOAK_ADMIN_USER/PASSWORD) tanımlı olmalı");
    }

    /// <summary>Servis hesabi (client_credentials) yapilandirilmis mi?</summary>
    public bool UsesServiceAccount => _clientSecret is not null;

    // ------------------------------------------------------------ token

    /// <summary>Onbellekteki jetonu gecersiz kilar (orn. 401 sonrasi).</summary>
    public static void InvalidateToken() => _cached = null;

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var cached = _cached;
        if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.Value;

        await TokenLock.WaitAsync(ct);
        try
        {
            // Bekleyen diger istek bu arada yenilemis olabilir.
            cached = _cached;
            if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresAt)
                return cached.Value;

            string? json = null;
            if (_clientSecret is not null)
            {
                using var resp = await _http.PostAsync(
                    $"{_baseUrl}/realms/{_realm}/protocol/openid-connect/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = _clientId,
                        ["client_secret"] = _clientSecret,
                    }), ct);
                if (resp.IsSuccessStatusCode)
                    json = await resp.Content.ReadAsStringAsync(ct);
                else if (string.IsNullOrEmpty(_adminUser) || string.IsNullOrEmpty(_adminPassword)
                         || (int)resp.StatusCode >= 500)
                    throw new InvalidOperationException(
                        $"Keycloak servis hesabı jetonu alınamadı ({(int)resp.StatusCode})");
                else
                    // Istemci Keycloak'ta henuz yok / anahtar uyusmuyor (guncellemede
                    // scripts/keycloak-service-account.sh calistirilmadan once): eski yola dus.
                    _logger.LogWarning(
                        "Keycloak servis hesabı ({ClientId}) reddedildi ({Status}); geçici olarak yönetici parola girişi kullanılıyor. scripts/keycloak-service-account.sh çalıştırın.",
                        _clientId, (int)resp.StatusCode);
            }

            if (json is null)
            {
                if (Interlocked.Exchange(ref _legacyWarned, 1) == 0 && _clientSecret is null)
                    _logger.LogWarning(
                        "KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET tanımlı değil: Keycloak yönetim API'sine master yönetici parolasıyla giriliyor (eski yol). scripts/keycloak-service-account.sh ile servis hesabına geçin.");
                using var resp = await _http.PostAsync(
                    $"{_baseUrl}/realms/master/protocol/openid-connect/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "password",
                        ["client_id"] = "admin-cli",
                        ["username"] = _adminUser!,
                        ["password"] = _adminPassword!,
                    }), ct);
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(ct);
            }

            using var doc = JsonDocument.Parse(json);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 60;
            // Erken yenile: sinira dayanmadan 30 saniye once gecersiz say.
            _cached = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, expiresIn - 30)));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private async Task<HttpRequestMessage> BuildAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}/admin/realms/{_realm}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        if (body is not null)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        }
        return req;
    }

    /// <summary>Location header'indan olusturulan kaynagin kimligini cikarir.</summary>
    private static string? IdFromLocation(HttpResponseMessage resp)
        => resp.Headers.Location?.Segments.LastOrDefault()?.Trim('/');

    // ---------------------------------------------------- organizations

    public async Task<string> CreateOrganizationAsync(
        string name, string alias, string? emailDomain, CancellationToken ct)
    {
        var domains = string.IsNullOrWhiteSpace(emailDomain)
            ? new[] { new { name = $"{alias}.hr360.local" } }
            : new[] { new { name = emailDomain } };

        // NOT: Keycloak'in (bu projede kullanilan surum: 25.0) Organizations Admin
        // REST API'sinde "alias" DIYE BIR ALAN YOK - OrganizationRepresentation'in
        // bildigi tek alanlar: enabled, identityProviders, attributes, members, id,
        // description, domains, name. Eskiden burada gonderilen "alias" alani sunucu
        // tarafindan sessizce degil, ACIKCA 400 Bad Request ile reddediliyordu -
        // yani sirket kayit sihirbazinin TUMU (ProvisionAsync -> bu metod) HER
        // ZAMAN patliyordu ve telafi/rollback mantigi devreye girip olusturulan
        // tenant kaydini geri aliyordu (hardcore test sirasinda bulundu - demo
        // tenant seed'i denerken ayni hatayla karsilasildi).
        //
        // Keycloak, oturum acan kullanicinin JWT'sindeki "organization" claim'ini
        // organizasyonun "name" alaniyla anahtarliyor (alias/slug DEGIL - Keycloak'ta
        // boyle bir kavram yok). MyTenantController ise bu claim'i tenant'in Slug'iyla
        // eslestiriyor. Bu yuzden Keycloak organizasyonunun "name" alanina insan-okur
        // gorunen adi degil, SLUG'i ("alias" parametresi) yaziyoruz; insan-okur adi
        // "description" alaninda saklaniyor.
        var req = await BuildAsync(HttpMethod.Post, "/organizations",
            new { name = alias, description = name, enabled = true, domains }, ct);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Keycloak organizasyon oluşturulamadı ({(int)resp.StatusCode}): {err}");
        }

        var id = IdFromLocation(resp)
            ?? throw new InvalidOperationException("Organizasyon kimligi Location header'inda yok");
        _logger.LogInformation("Keycloak organizasyonu oluşturuldu: {Alias} ({Id})", alias, id);
        return id;
    }

    public async Task DeleteOrganizationAsync(string orgId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Delete, $"/organizations/{orgId}", null, ct);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Organizasyon silinemedi {Id}: {Status}", orgId, resp.StatusCode);
    }

    public async Task AddOrganizationMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/admin/realms/{_realm}/organizations/{orgId}/members");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        // NOT: Bu uc gecerli bir JSON string (tirnak icinde, orn. "\"<id>\"") GONDERILDIGINDE
        // Keycloak 25.0.6'da HER ZAMAN "User does not exist" (400) doner - kullanici
        // gercekten var ve id dogru olsa bile (canli sunucuya karsi curl/http.client ile
        // dogrulandi: tirnaksiz DUZ METIN govde 201 donuyor, ayni govde JSON-tirnakli
        // haliyle 400 donuyor). Bu, Keycloak'in bu uctaki govdeyi JSON olarak degil DUZ
        // METIN olarak parse etmesinden kaynaklaniyor; @Content-Type application/json
        // yine de gerekli (text/plain 415 donuyor). Sirket kayit sihirbazinin (ve demo
        // tenant seed'inin) HER ZAMAN "Kullanıcı organizasyona eklenemedi (400): User
        // does not exist" ile patlamasina sebep olan asil kok neden buydu - hardcore
        // test sirasinda bulundu ve canli Keycloak'a karsi curl ile dogrulandi.
        req.Content = new StringContent(userId, Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Kullanıcı organizasyona eklenemedi ({(int)resp.StatusCode}): {err}");
        }
    }

    // ----------------------------------------------------------- users

    /// <summary>
    /// Kullanici bu Keycloak organizasyonunun uyesi mi? (200 = uye, 404 = degil).
    /// Rol/izin degisikliklerinden once hedefin CAGIRANIN kiracisina ait
    /// oldugunu dogrulamak icin kullanilir - realm rolleri tum kiracilarda
    /// ortak oldugundan bu kontrol olmadan bir kiracinin yoneticisi baska
    /// bir kiracinin kullanicisina rol verebiliyordu.
    /// </summary>
    public async Task<bool> IsOrganizationMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgId) || string.IsNullOrWhiteSpace(userId)) return false;
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_baseUrl}/admin/realms/{_realm}/organizations/{Uri.EscapeDataString(orgId)}/members/{Uri.EscapeDataString(userId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Organizasyon uyeligi dogrulanamadi ({(int)resp.StatusCode})");
        return true;
    }

    public async Task<string?> FindUserByEmailAsync(string email, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get,
            $"/users?email={Uri.EscapeDataString(email)}&exact=true", null, ct);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0].GetProperty("id").GetString()
            : null;
    }

    /// <summary>
    /// Parolasiz kullanici olusturur. Kullanici, UPDATE_PASSWORD required action'i
    /// ile ilk giriste parolasini kendisi belirler - parola hicbir yerde uretilmez
    /// veya saklanmaz.
    /// </summary>
    public async Task<string> CreateUserAsync(
        string email, string? firstName, string? lastName, Guid tenantId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Post, "/users", new
        {
            username = email,
            email,
            firstName = firstName ?? "",
            lastName = lastName ?? "",
            enabled = true,
            emailVerified = false,
            requiredActions = new[] { "UPDATE_PASSWORD" },
            // Tenant kimligini kullanici ozniteligi olarak da tut: organization
            // claim'i alias tasir, servislerin GUID'e ihtiyaci olursa buradan alinir.
            attributes = new Dictionary<string, string[]>
            {
                ["tenant_id"] = new[] { tenantId.ToString() },
            },
        }, ct);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Kullanıcı oluşturulamadı ({(int)resp.StatusCode}): {err}");
        }

        return IdFromLocation(resp)
            ?? throw new InvalidOperationException("Kullanıcı kimliği Location header'ında yok");
    }

    public async Task AssignRealmRoleAsync(string userId, string roleName, CancellationToken ct)
    {
        var role = await GetRealmRoleAsync(roleName, ct)
            ?? throw new InvalidOperationException($"Rol bulunamadi: {roleName}");

        var req = await BuildAsync(
            HttpMethod.Post, $"/users/{userId}/role-mappings/realm", new[] { role }, ct);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Rol atanamadi ({(int)resp.StatusCode}): {err}");
        }
    }

    /// <summary>Bir kullanicidan realm rolunu kaldirir. Keycloak DELETE
    /// body'sinin de kaldirilacak rolun {id, name} bilgisini tasimasini
    /// bekler - GET ile ayni sekilde once rolu cozup gonderiyoruz.</summary>
    public async Task RemoveRealmRoleAsync(string userId, string roleName, CancellationToken ct)
    {
        var role = await GetRealmRoleAsync(roleName, ct)
            ?? throw new InvalidOperationException($"Rol bulunamadi: {roleName}");

        var req = new HttpRequestMessage(
            HttpMethod.Delete, $"{_baseUrl}/admin/realms/{_realm}/users/{userId}/role-mappings/realm");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        req.Content = new StringContent(
            JsonSerializer.Serialize(new[] { role }, JsonOpts), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Rol kaldirilamadi ({(int)resp.StatusCode}): {err}");
        }
    }

    /// <summary>Bir kullanicinin su an sahip oldugu realm rollerinin adlarini dondurur.</summary>
    public async Task<List<string>> GetUserRealmRolesAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get, $"/users/{userId}/role-mappings/realm", null, ct);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new List<string>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray()
            .Select(r => r.GetProperty("name").GetString() ?? "")
            .Where(n => n.Length > 0)
            .ToList();
    }

    private async Task<object?> GetRealmRoleAsync(string roleName, CancellationToken ct)
    {
        var roleReq = await BuildAsync(HttpMethod.Get, $"/roles/{roleName}", null, ct);
        var roleResp = await _http.SendAsync(roleReq, ct);
        if (!roleResp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await roleResp.Content.ReadAsStringAsync(ct));
        return new
        {
            id = doc.RootElement.GetProperty("id").GetString(),
            name = doc.RootElement.GetProperty("name").GetString(),
        };
    }

    /// <summary>
    /// "Ek izin" rolleri (ext-&lt;permission&gt;, orn. ext-compensation-view)
    /// icin - bu roller sabit 5 roldeki (employee/manager/accounting/
    /// hr-admin/tenant-admin) gibi Keycloak'ta onceden elle olusturulmus
    /// degil, ilk atamada kendiliginden yaratilir. Zaten varsa (rol id'si
    /// donerse) hicbir sey yapmaz - iki farkli kullaniciya ayni ek izni
    /// atarken ayni rolu iki kez olusturmaya calismak hataya yol acmamali.
    /// </summary>
    public async Task EnsureRealmRoleExistsAsync(string roleName, CancellationToken ct)
    {
        var existing = await GetRealmRoleAsync(roleName, ct);
        if (existing is not null) return;

        var req = await BuildAsync(HttpMethod.Post, "/roles", new { name = roleName }, ct);
        var resp = await _http.SendAsync(req, ct);
        // 409 Conflict: baska bir istek ayni anda olusturmus olabilir - o
        // da basarili sayilir, rol zaten var demektir.
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"'{roleName}' rolu olusturulamadi ({(int)resp.StatusCode}): {err}");
        }
    }

    /// <summary>
    /// Parola belirleme e-postasi gonderir. Keycloak'ta SMTP yapilandirilmis
    /// olmalidir; degilse bu adim basarisiz olur ama kayit gecerli kalir
    /// (yonetici parolayi elle sifirlayabilir).
    /// </summary>
    public async Task SendPasswordSetupEmailAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Put,
            $"/users/{userId}/execute-actions-email?lifespan=86400",
            new[] { "UPDATE_PASSWORD" }, ct);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Parola e-postasi gonderilemedi ({(int)resp.StatusCode}): {err}");
        }
    }

    /// <summary>Parola belirleme/sıfırlama linki için hesap özeti (null: kullanıcı yok).</summary>
    public sealed record UserBasics(string Id, string? Email, string? FirstName, string? LastName, bool Enabled, string? Locale);

    public async Task<UserBasics?> GetUserBasicsAsync(string userId, CancellationToken ct)
    {
        var u = await GetUserRepresentationAsync(userId, ct);
        if (u is null) return null;
        string? locale = null;
        if (u["attributes"] is System.Text.Json.Nodes.JsonObject attrs && attrs["locale"] is System.Text.Json.Nodes.JsonArray la && la.Count > 0)
            locale = la[0]?.GetValue<string>();
        return new UserBasics(userId, u["email"]?.GetValue<string>(), u["firstName"]?.GetValue<string>(),
            u["lastName"]?.GetValue<string>(), u["enabled"]?.GetValue<bool>() ?? false, locale);
    }

    /// <summary>
    /// Parolayı belirler (kalıcı, temporary=false). Keycloak parola politikası reddederse
    /// <see cref="PasswordPolicyException"/> (Keycloak'ın hata kodu ve açıklamasıyla) fırlatır.
    /// </summary>
    public async Task ResetPasswordAsync(string userId, string password, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Put, $"/users/{Uri.EscapeDataString(userId)}/reset-password",
            new { type = "password", value = password, temporary = false }, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            string? code = null, description = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) code = e.GetString();
                if (doc.RootElement.TryGetProperty("error_description", out var d)) description = d.GetString();
            }
            catch (JsonException) { }
            throw new PasswordPolicyException(code ?? "invalidPassword", description);
        }
        throw new InvalidOperationException($"Parola belirlenemedi ({(int)resp.StatusCode})");
    }

    /// <summary>Parola belirlendikten sonra: UPDATE_PASSWORD gereksinimini kaldırır, istenirse e-postayı doğrulanmış işaretler.</summary>
    public async Task CompletePasswordSetupAsync(string userId, bool markEmailVerified, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return;
        var changed = false;
        if (user["requiredActions"] is System.Text.Json.Nodes.JsonArray actions)
        {
            var keep = new System.Text.Json.Nodes.JsonArray();
            foreach (var a in actions)
            {
                var v = a?.GetValue<string>();
                if (v == "UPDATE_PASSWORD") { changed = true; continue; }
                keep.Add(v);
            }
            if (changed) user["requiredActions"] = keep;
        }
        if (markEmailVerified && user["emailVerified"]?.GetValue<bool>() != true)
        {
            user["emailVerified"] = true;
            changed = true;
        }
        if (changed) await PutUserAsync(userId, user, ct);
    }

    /// <summary>
    /// Realm ayarları: Keycloak'ın kendi "parolamı unuttum" bağlantısı (resetPasswordAllowed) ve
    /// e-posta sunucusu (smtpServer; eski yöntem "Keycloak e-postası" bunu kullanır). smtp null ise
    /// realm SMTP ayarı temizlenir.
    /// </summary>
    public async Task ApplyRealmMailSettingsAsync(bool resetPasswordAllowed, Dictionary<string, string>? smtp, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Put, "", new
        {
            resetPasswordAllowed,
            smtpServer = smtp ?? new Dictionary<string, string>(),
        }, ct);
    }

    public async Task DeleteUserAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Delete, $"/users/{userId}", null, ct);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Kullanıcı silinemedi {Id}: {Status}", userId, resp.StatusCode);
    }

    /// <summary>Organizasyon uyesi (Keycloak kullanici ozeti; parola/oznitelik yok).</summary>
    public sealed record OrgMember(string Id, string? Username, string? Email, string? FirstName, string? LastName, bool Enabled);

    /// <summary>Organizasyonun tum uye kimlikleri (sayfali).</summary>
    public async Task<List<string>> ListOrganizationMemberIdsAsync(string orgId, CancellationToken ct) =>
        (await ListOrganizationMembersAsync(orgId, ct)).Select(m => m.Id).ToList();

    /// <summary>Organizasyonun tum uyeleri: kimlik, kullanici adi, e-posta, ad (sayfali).</summary>
    public async Task<List<OrgMember>> ListOrganizationMembersAsync(string orgId, CancellationToken ct)
    {
        var list = new List<OrgMember>();
        const int page = 100;
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        for (var first = 0; ; first += page)
        {
            var req = await BuildAsync(HttpMethod.Get,
                $"/organizations/{Uri.EscapeDataString(orgId)}/members?first={first}&max={page}", null, ct);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Organizasyon uyeleri alinamadi ({(int)resp.StatusCode})");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var count = 0;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                count++;
                if (Str(m, "id") is not { } id) continue;
                var enabled = !m.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                list.Add(new OrgMember(id, Str(m, "username"), Str(m, "email"), Str(m, "firstName"), Str(m, "lastName"), enabled));
            }
            if (count < page) break;
        }
        return list;
    }

    /// <summary>
    /// Kiraci askiya alinirken: hesap aciksa kapatir ve tum oturumlarini sonlandirir
    /// (yenileme jetonlari da gecersiz olur). Donus: hesap BU cagriyla mi kapatildi
    /// (zaten kapali hesaba dokunulmaz - yeniden etkinlestirmede acilmasin diye).
    ///
    /// NOT: Guncelleme kullanicinin TAM temsiliyle yapilir. Keycloak 25'te e-posta/ad
    /// kullanici profili nitelikleridir; yalnizca {enabled, attributes} gondermek
    /// e-posta ve adi SILIYORDU (canli testte yakalandi).
    /// </summary>
    public async Task<bool> SuspendUserForTenantAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return false;
        var wasEnabled = user["enabled"]?.GetValue<bool>() == true;
        if (wasEnabled)
        {
            user["enabled"] = false;
            await PutUserAsync(userId, user, ct);
        }
        var logout = await BuildAsync(HttpMethod.Post, $"/users/{Uri.EscapeDataString(userId)}/logout", null, ct);
        using (var resp = await _http.SendAsync(logout, ct))
        {
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Oturumlar sonlandirilamadi {Id}: {Status}", userId, (int)resp.StatusCode);
        }
        return wasEnabled;
    }

    /// <summary>Askiya alma sirasinda kapatilmis hesabi geri acar (tam temsille).</summary>
    public async Task<bool> EnableUserAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return false;
        if (user["enabled"]?.GetValue<bool>() == true) return false;
        user["enabled"] = true;
        await PutUserAsync(userId, user, ct);
        return true;
    }

    private async Task<System.Text.Json.Nodes.JsonObject?> GetUserRepresentationAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(userId)}", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kullanici okunamadi ({(int)resp.StatusCode})");
        return System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as System.Text.Json.Nodes.JsonObject;
    }

    private async Task PutUserAsync(string userId, System.Text.Json.Nodes.JsonObject body, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Put, $"/users/{Uri.EscapeDataString(userId)}", null, ct);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kullanici guncellenemedi ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
    }

    public async Task SetUserEnabledAsync(string userId, bool enabled, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Put, $"/users/{userId}", new { enabled }, ct);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Kullanıcı durumu değiştirilemedi {Id}", userId);
    }

    // ------------------------------------------------- SSO (kimlik sağlayıcı aracılığı)

    /// <summary>Organizasyona bağlı dış kimlik sağlayıcıları (Google, Azure AD…).</summary>
    public async Task<List<JsonElement>> ListOrganizationIdentityProvidersAsync(string orgId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get, $"/organizations/{Uri.EscapeDataString(orgId)}/identity-providers", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>
    /// Realm'e bir kimlik sağlayıcı ekler ve organizasyona bağlar. Bağlandığında
    /// Keycloak giriş ekranında, e-posta alanı organizasyonun alan adıyla eşleşen
    /// kullanıcıyı doğrudan bu sağlayıcıya yönlendirir (identity-first login).
    /// </summary>
    public async Task CreateOrganizationIdentityProviderAsync(
        string orgId, string alias, string displayName, string providerId, Dictionary<string, string> config, CancellationToken ct)
    {
        var create = await BuildAsync(HttpMethod.Post, "/identity-provider/instances", new
        {
            alias, displayName, providerId, enabled = true, trustEmail = true, storeToken = false,
            firstBrokerLoginFlowAlias = "first broker login", config,
        }, ct);
        using (var resp = await _http.SendAsync(create, ct))
        {
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Conflict)
                throw new InvalidOperationException($"Kimlik sağlayıcı oluşturulamadı ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
        }
        var link = await BuildAsync(HttpMethod.Post, $"/organizations/{Uri.EscapeDataString(orgId)}/identity-providers", null, ct);
        // Keycloak 25 gövdeyi ham metin olarak okur: JSON tırnaklı ("alias") gönderilirse
        // tırnaklar takma adın parçası sayılıp "bulunamadı" (400) döner.
        link.Content = new StringContent(alias, Encoding.UTF8, "application/json");
        using var linkResp = await _http.SendAsync(link, ct);
        if (!linkResp.IsSuccessStatusCode && linkResp.StatusCode != System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException($"Kimlik sağlayıcı organizasyona bağlanamadı ({(int)linkResp.StatusCode}): {await linkResp.Content.ReadAsStringAsync(ct)}");
    }

    public async Task DeleteIdentityProviderAsync(string orgId, string alias, CancellationToken ct)
    {
        var unlink = await BuildAsync(HttpMethod.Delete, $"/organizations/{Uri.EscapeDataString(orgId)}/identity-providers/{Uri.EscapeDataString(alias)}", null, ct);
        using (await _http.SendAsync(unlink, ct)) { }
        var del = await BuildAsync(HttpMethod.Delete, $"/identity-provider/instances/{Uri.EscapeDataString(alias)}", null, ct);
        using var resp = await _http.SendAsync(del, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Kimlik sağlayıcı silinemedi ({(int)resp.StatusCode})");
    }

    // ----------------------------------------------------------------- MFA

    /// <summary>Kullanıcının OTP (TOTP) kimlik bilgisi var mı, kurulum bekliyor mu?</summary>
    public async Task<(bool HasOtp, bool PendingSetup, string? Username)> GetOtpStatusAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return (false, false, null);
        var pending = user["requiredActions"] is System.Text.Json.Nodes.JsonArray ra && ra.Any(a => a?.GetValue<string>() == "CONFIGURE_TOTP");
        var req = await BuildAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(userId)}/credentials", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        var hasOtp = false;
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            hasOtp = doc.RootElement.EnumerateArray().Any(c => c.TryGetProperty("type", out var t) && t.GetString() == "otp");
        }
        return (hasOtp, pending, user["username"]?.GetValue<string>());
    }

    /// <summary>
    /// Kullanıcının Keycloak dilini (locale özniteliği) ayarlar: giriş ekranı, parola
    /// sıfırlama ve davet e-postaları bu dilde gelir.
    /// </summary>
    public async Task<bool> SetUserLocaleAsync(string userId, string locale, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return false;
        var attrs = user["attributes"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        attrs["locale"] = new System.Text.Json.Nodes.JsonArray(locale);
        user["attributes"] = attrs.DeepClone();
        await PutUserAsync(userId, user, ct);
        return true;
    }

    /// <summary>Bir sonraki girişte doğrulayıcı uygulama kurulumunu zorunlu kılar.</summary>
    public async Task RequireOtpSetupAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return;
        var actions = user["requiredActions"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
        if (actions.Any(a => a?.GetValue<string>() == "CONFIGURE_TOTP")) return;
        actions.Add("CONFIGURE_TOTP");
        user["requiredActions"] = actions.DeepClone();
        await PutUserAsync(userId, user, ct);
    }

    // ------------------------------------------------------- oturumlar (G22)

    public sealed record SessionInfo(string Id, string? IpAddress, DateTimeOffset Start, DateTimeOffset LastAccess, IReadOnlyList<string> Clients);

    /// <summary>Kullanıcının etkin Keycloak oturumları (cihaz/IP, başlangıç, son erişim).</summary>
    public async Task<List<SessionInfo>> ListUserSessionsAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(userId)}/sessions", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Oturumlar okunamadı ({(int)resp.StatusCode})");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(s => new SessionInfo(
            s.GetProperty("id").GetString()!,
            s.TryGetProperty("ipAddress", out var ip) ? ip.GetString() : null,
            DateTimeOffset.FromUnixTimeMilliseconds(s.TryGetProperty("start", out var st) ? st.GetInt64() : 0),
            DateTimeOffset.FromUnixTimeMilliseconds(s.TryGetProperty("lastAccess", out var la) ? la.GetInt64() : 0),
            s.TryGetProperty("clients", out var c) && c.ValueKind == JsonValueKind.Object
                ? c.EnumerateObject().Select(p => p.Value.GetString() ?? p.Name).ToList() : new List<string>())).ToList();
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Delete, $"/sessions/{Uri.EscapeDataString(sessionId)}", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Oturum kapatılamadı ({(int)resp.StatusCode})");
    }

    public async Task LogoutUserAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Post, $"/users/{Uri.EscapeDataString(userId)}/logout", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Oturumlar kapatılamadı ({(int)resp.StatusCode})");
    }

    /// <summary>Kullanıcının kayıtlı kimlik bilgisi türleri (password, otp, webauthn...).</summary>
    public async Task<List<string>> GetCredentialTypesAsync(string userId, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(userId)}/credentials", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(c => c.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "").Where(t => t != "").ToList();
    }

    public async Task<bool> AddRequiredActionAsync(string userId, string action, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return false;
        var actions = user["requiredActions"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
        if (actions.Any(a => a?.GetValue<string>() == action)) return true;
        actions.Add(action);
        user["requiredActions"] = actions.DeepClone();
        await PutUserAsync(userId, user, ct);
        return true;
    }

    // ------------------------------------------------------- passkey / WebAuthn (G22)

    public const string PasskeyFlowAlias = "hr360 browser";

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get, path, null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Keycloak {path} okunamadı ({(int)resp.StatusCode})");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    private async Task SendAsync(HttpMethod m, string path, object? body, CancellationToken ct, bool allowConflict = false)
    {
        var req = await BuildAsync(m, path, body, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode || (allowConflict && resp.StatusCode == System.Net.HttpStatusCode.Conflict)) return;
        throw new InvalidOperationException($"Keycloak {m} {path} başarısız ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>
    /// Giriş akışına güvenlik anahtarı / passkey (WebAuthn) ikinci adımını ekler: varsayılan
    /// "browser" akışının kopyasında koşullu OTP alt akışına WebAuthn doğrulayıcısı
    /// ALTERNATIVE olarak eklenir. Kullanıcı OTP ya da passkey'den birini kullanabilir;
    /// ikisini de kurmamış kullanıcının girişi değişmez. Idempotent.
    /// </summary>
    public async Task<bool> EnsurePasskeyFlowAsync(CancellationToken ct)
    {
        var flows = await GetJsonAsync("/authentication/flows", ct);
        var exists = flows.EnumerateArray().Any(f => f.GetProperty("alias").GetString() == PasskeyFlowAlias);
        var alias = Uri.EscapeDataString(PasskeyFlowAlias);
        if (!exists)
            await SendAsync(HttpMethod.Post, "/authentication/flows/browser/copy", new { newName = PasskeyFlowAlias }, ct, allowConflict: true);

        var execs = await GetJsonAsync($"/authentication/flows/{alias}/executions", ct);
        var sub = execs.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("authenticationFlow", out var af) && af.GetBoolean()
            && (e.GetProperty("displayName").GetString() ?? "").Contains("Conditional OTP", StringComparison.OrdinalIgnoreCase));
        if (sub.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Koşullu OTP alt akışı bulunamadı");
        var subAlias = sub.GetProperty("displayName").GetString()!;
        var subLevel = sub.GetProperty("level").GetInt32();
        bool InSub(JsonElement e) => e.GetProperty("level").GetInt32() == subLevel + 1;

        if (!execs.EnumerateArray().Any(e => InSub(e) && e.TryGetProperty("providerId", out var p) && p.GetString() == "webauthn-authenticator"))
        {
            await SendAsync(HttpMethod.Post, $"/authentication/flows/{Uri.EscapeDataString(subAlias)}/executions/execution",
                new { provider = "webauthn-authenticator" }, ct);
            execs = await GetJsonAsync($"/authentication/flows/{alias}/executions", ct);
        }
        foreach (var e in execs.EnumerateArray().Where(InSub))
        {
            var pid = e.TryGetProperty("providerId", out var p) ? p.GetString() : null;
            if (pid is not ("auth-otp-form" or "webauthn-authenticator")) continue;
            if (e.GetProperty("requirement").GetString() == "ALTERNATIVE") continue;
            await SendAsync(HttpMethod.Put, $"/authentication/flows/{alias}/executions",
                new { id = e.GetProperty("id").GetString(), requirement = "ALTERNATIVE" }, ct);
        }

        // "Güvenlik anahtarı kaydet" gerekli eylemi açık olmalı (uygulamadan başlatılır: kc_action).
        var actions = await GetJsonAsync("/authentication/required-actions", ct);
        var reg = actions.EnumerateArray().FirstOrDefault(a => a.GetProperty("alias").GetString() == "webauthn-register");
        if (reg.ValueKind == JsonValueKind.Undefined)
        {
            await SendAsync(HttpMethod.Post, "/authentication/register-required-action", new { providerId = "webauthn-register", name = "Webauthn Register" }, ct, allowConflict: true);
            reg = (await GetJsonAsync("/authentication/required-actions", ct)).EnumerateArray().First(a => a.GetProperty("alias").GetString() == "webauthn-register");
        }
        if (!reg.GetProperty("enabled").GetBoolean())
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(reg.GetRawText())!.AsObject();
            node["enabled"] = true;
            var req = await BuildAsync(HttpMethod.Put, "/authentication/required-actions/webauthn-register", null, ct);
            req.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            using var r = await _http.SendAsync(req, ct);
            r.EnsureSuccessStatusCode();
        }

        var realm = await GetJsonAsync("", ct);
        if (realm.TryGetProperty("browserFlow", out var bf) && bf.GetString() == PasskeyFlowAlias) return !exists;
        await SendAsync(HttpMethod.Put, "", new { browserFlow = PasskeyFlowAlias }, ct);
        return true;
    }

    // ------------------------------------------------- Dalga 5d: dizin sağlama + özel alan adı

    /// <summary>
    /// Dizin (SCIM/LDAP) kaynakli ad/e-posta guncellemesi. Keycloak 25'te e-posta/ad kullanici
    /// profili nitelikleri oldugu icin TAM temsil okunup yalnizca bu alanlar degistirilir
    /// (kismi PUT diger nitelikleri siliyordu - bkz. SuspendUserForTenantAsync).
    /// Kullanici adi (giris adi) degistirilmez. Donus: kullanici bulundu mu.
    /// </summary>
    public async Task<bool> UpdateUserProfileAsync(string userId, string email, string? firstName, string? lastName, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return false;
        var changed = false;
        void Set(string key, string? value)
        {
            if (value is null) return;
            if (user[key]?.GetValue<string>() == value) return;
            user[key] = value;
            changed = true;
        }
        if (!string.Equals(user["email"]?.GetValue<string>(), email, StringComparison.OrdinalIgnoreCase))
        {
            Set("email", email);
            user["emailVerified"] = false;
        }
        Set("firstName", firstName);
        Set("lastName", lastName);
        if (changed) await PutUserAsync(userId, user, ct);
        return true;
    }

    /// <summary>Hesap su an acik mi (null: kullanici yok).</summary>
    public async Task<bool?> IsUserEnabledAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        return user is null ? null : user["enabled"]?.GetValue<bool>() == true;
    }

    /// <summary>
    /// G28: dogrulanan ozel alan adini uygulama istemcisinin (hr360-web) yonlendirme
    /// adreslerine / web kokenlerine ekler ya da cikarir; boylece kiraci kendi alan adindan
    /// giris yapip geri donebilir. Idempotent. Platform adresine (PUBLIC_ORIGIN) dokunmaz.
    /// </summary>
    public async Task<bool> SetWebClientOriginAsync(string origin, bool present, CancellationToken ct)
    {
        var clientId = Environment.GetEnvironmentVariable("KEYCLOAK_WEB_CLIENT_ID") ?? "hr360-web";
        var list = await GetJsonAsync($"/clients?clientId={Uri.EscapeDataString(clientId)}", ct);
        var first = list.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined) return false;
        var node = System.Text.Json.Nodes.JsonNode.Parse(first.GetRawText())!.AsObject();
        var id = node["id"]!.GetValue<string>();
        var redirect = origin + "/*";
        var changed = false;

        bool Edit(string key, string value)
        {
            var arr = node[key] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            var existing = arr.Select(x => x?.GetValue<string>()).ToList();
            var has = existing.Contains(value);
            if (present == has) return false;
            var updated = new System.Text.Json.Nodes.JsonArray();
            foreach (var v in existing.Where(v => v is not null && v != value)) updated.Add(v);
            if (present) updated.Add(value);
            node[key] = updated;
            return true;
        }
        changed |= Edit("redirectUris", redirect);
        changed |= Edit("webOrigins", origin);

        var attrs = node["attributes"] as System.Text.Json.Nodes.JsonObject;
        if (attrs is not null)
        {
            var post = (attrs["post.logout.redirect.uris"]?.GetValue<string>() ?? "")
                .Split("##", StringSplitOptions.RemoveEmptyEntries).ToList();
            var has = post.Contains(redirect);
            if (present != has)
            {
                if (present) post.Add(redirect); else post.Remove(redirect);
                attrs["post.logout.redirect.uris"] = string.Join("##", post);
                changed = true;
            }
        }
        if (!changed) return true;
        var req = await BuildAsync(HttpMethod.Put, $"/clients/{Uri.EscapeDataString(id)}", null, ct);
        req.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Uygulama istemcisi güncellenemedi ({(int)resp.StatusCode})");
        return true;
    }

    // ------------------------------------------------- Guvenlik dalgasi 2A: MFA politikasi, olaylar

    /// <summary>Realm rolune DOGRUDAN sahip kullanicilarin kimlikleri (sayfali; grup/bilesik rol haric).</summary>
    public async Task<HashSet<string>> ListRoleUserIdsAsync(string roleName, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        const int page = 200;
        for (var first = 0; ; first += page)
        {
            var req = await BuildAsync(HttpMethod.Get,
                $"/roles/{Uri.EscapeDataString(roleName)}/users?first={first}&max={page}&briefRepresentation=true", null, ct);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return ids;
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Rol kullanıcıları okunamadı ({(int)resp.StatusCode})");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var count = 0;
            foreach (var u in doc.RootElement.EnumerateArray())
            {
                count++;
                if (u.TryGetProperty("id", out var id) && id.GetString() is { } v) ids.Add(v);
            }
            if (count < page) break;
        }
        return ids;
    }

    public sealed record MfaState(string? Username, IReadOnlyList<string> CredentialTypes, IReadOnlyList<string> RequiredActions);

    /// <summary>Kullanicinin adi, kimlik bilgisi turleri ve bekleyen gerekli eylemleri (null: kullanici yok).</summary>
    public async Task<MfaState?> GetUserMfaStateAsync(string userId, CancellationToken ct)
    {
        var user = await GetUserRepresentationAsync(userId, ct);
        if (user is null) return null;
        var actions = (user["requiredActions"] as System.Text.Json.Nodes.JsonArray)?
            .Select(a => a?.GetValue<string>() ?? "").Where(a => a != "").ToList() ?? new List<string>();
        return new MfaState(user["username"]?.GetValue<string>(), await GetCredentialTypesAsync(userId, ct), actions);
    }

    public sealed record LoginEvent(long Time, string Type, string? UserId, string? IpAddress, string? Error, string? ClientId, string? Username);

    /// <summary>
    /// Realm'in LOGIN / LOGIN_ERROR olaylari (yeniden eskiye). Keycloak olay deposu
    /// (eventsEnabled, 30 gun) acik olmali; servis hesabinda view-events rolu gerekir.
    /// </summary>
    public async Task<List<LoginEvent>> ListLoginEventsAsync(DateOnly dateFrom, int first, int max, CancellationToken ct)
    {
        var req = await BuildAsync(HttpMethod.Get,
            $"/events?type=LOGIN&type=LOGIN_ERROR&dateFrom={dateFrom:yyyy-MM-dd}&first={first}&max={max}", null, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Keycloak giriş olayları okunamadı ({(int)resp.StatusCode})");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return doc.RootElement.EnumerateArray().Select(e => new LoginEvent(
            e.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
            Str(e, "type") ?? "",
            Str(e, "userId"),
            Str(e, "ipAddress"),
            Str(e, "error"),
            Str(e, "clientId"),
            e.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? Str(d, "username") : null)).ToList();
    }
}

/// <summary>Keycloak parola politikası parolayı reddetti (Code: Keycloak hata kodu, ör. invalidPasswordMinLengthMessage).</summary>
public sealed class PasswordPolicyException(string code, string? description) : Exception(description ?? code)
{
    public string Code { get; } = code;
    public string? Description { get; } = description;
}

