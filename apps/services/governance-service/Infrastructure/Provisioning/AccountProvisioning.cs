using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GovernanceService.Infrastructure.Calendar;
using GovernanceService.Infrastructure.Chat;
using Npgsql;

namespace GovernanceService.Infrastructure.Provisioning;

/* ======================================================================
 * Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma ve
 * askıya alma. Akış:
 *   1) Tarama (ProvisioningScanner, ya da İK "Şimdi tara"): işe girişi yakın
 *      çalışan için "Create", ayrılan çalışan için "Suspend" isteği Pending
 *      olarak açılır. Hiçbir çağrı otomatik yapılmaz.
 *   2) İK onayı (dört göz: elle açılan isteği açan kişi onaylayamaz; kimse
 *      kendi hesabı için karar veremez) → sağlayıcıya tek çağrı.
 *   3) Her adım audit_log'a yazılır (ComplianceAudit).
 * Kurulum düzeyinde ACCOUNT_PROVISIONING_ENABLED=true ile açılır; kiracı
 * ayrıca sağlayıcıyı yapılandırıp etkinleştirmelidir. Gerçek hesaplarla
 * denenmedi: testler tests/integration/chatmock.py sahte uçlarıyla çalışır.
 * ==================================================================== */

public static class ProvisioningFlag
{
    /// <summary>ACCOUNT_PROVISIONING_ENABLED=true: hesap açma/kapatma özelliği (varsayılan kapalı).</summary>
    public static bool Enabled =>
        (Environment.GetEnvironmentVariable("ACCOUNT_PROVISIONING_ENABLED") ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
}

public sealed record ProvisioningConfig(
    Guid Id, string TenantSlug, string Provider, bool IsEnabled, string Domain, bool AutoCreate, bool AutoSuspend,
    string? ClientId, string? CredentialsEnc, string? AdminSubject, string? OrgUnit, string? MsTenant, string? UsageLocation,
    DateTime? LastTestAt, string? LastError, DateTime CreatedAt);

public sealed record NewAccount(string PrimaryEmail, string GivenName, string FamilyName, string Password);

/// <summary>Sağlayıcı sözleşmesi: bağlantı testi, hesap açma (dış kimlik döner), askıya alma.</summary>
public interface IDirectoryProvisioner
{
    string Name { get; }
    Task TestAsync(ProvisioningConfig cfg, CancellationToken ct);
    Task<string> CreateUserAsync(ProvisioningConfig cfg, NewAccount a, CancellationToken ct);
    Task SuspendUserAsync(ProvisioningConfig cfg, string email, CancellationToken ct);
}

/// <summary>Ad → iş e-postası önerisi, doğrulama ve geçici parola (saf işlevler; birim testli).</summary>
public static partial class AccountNaming
{
    public static readonly string[] Providers = ["Google", "Microsoft"];

    public static string? NormalizeProvider(string? p) =>
        Providers.FirstOrDefault(x => x.Equals(p?.Trim(), StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainRx();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$")]
    private static partial Regex LocalRx();

    public static bool ValidDomain(string? d) => d is not null && DomainRx().IsMatch(d.Trim().ToLowerInvariant());

    /// <summary>Türkçe harfleri ASCII'ye çevirir, harf/rakam dışını atar ("Şükrü Öz" → "sukruoz").</summary>
    public static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            var c = ch switch
            {
                'ı' or 'I' or 'İ' or 'i' => 'i', 'ş' or 'Ş' => 's', 'ğ' or 'Ğ' => 'g', 'ü' or 'Ü' => 'u',
                'ö' or 'Ö' => 'o', 'ç' or 'Ç' => 'c', 'â' or 'Â' => 'a', 'î' or 'Î' => 'i', 'û' or 'Û' => 'u',
                _ => char.ToLowerInvariant(ch),
            };
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else
            {
                // Diğer aksanlı harfler (é, ñ...) temel harfe indirgenir.
                var d = c.ToString().Normalize(NormalizationForm.FormD);
                foreach (var x in d) if (x is >= 'a' and <= 'z') sb.Append(x);
            }
        }
        return sb.ToString();
    }

    /// <summary>"Ayşe Nur" + "Yılmaz" + "sirket.com" → "aysenur.yilmaz@sirket.com". Ad boşsa null.</summary>
    public static string? Propose(string firstName, string lastName, string domain)
    {
        var f = Ascii(firstName.Replace(" ", ""));
        var l = Ascii(lastName.Replace(" ", ""));
        var local = f.Length == 0 ? l : l.Length == 0 ? f : $"{f}.{l}";
        if (local.Length == 0) return null;
        if (local.Length > 64) local = local[..64].TrimEnd('.');
        return $"{local}@{domain.Trim().ToLowerInvariant()}";
    }

    /// <summary>Adres yapılandırılmış alan adında ve geçerli yerel kısma sahip mi? Başka alan adına hesap açılamaz.</summary>
    public static bool ValidAccountEmail(string? email, string domain)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var e = email.Trim().ToLowerInvariant();
        var at = e.LastIndexOf('@');
        if (at <= 0) return false;
        return e[(at + 1)..] == domain.Trim().ToLowerInvariant() && LocalRx().IsMatch(e[..at]) && !e[..at].Contains("..");
    }

    /// <summary>16 karakterlik geçici parola (büyük/küçük harf, rakam, sembol; ilk girişte değiştirilir). Saklanmaz.</summary>
    public static string TemporaryPassword()
    {
        const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", lo = "abcdefghijkmnpqrstuvwxyz", di = "23456789", sy = "!#$%*+-=?@";
        const string all = up + lo + di + sy;
        var chars = new List<char>
        {
            up[RandomNumberGenerator.GetInt32(up.Length)], lo[RandomNumberGenerator.GetInt32(lo.Length)],
            di[RandomNumberGenerator.GetInt32(di.Length)], sy[RandomNumberGenerator.GetInt32(sy.Length)],
        };
        while (chars.Count < 16) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        // Fisher–Yates
        for (var i = chars.Count - 1; i > 0; i--) { var j = RandomNumberGenerator.GetInt32(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars.ToArray());
    }

    /// <summary>Google servis hesabı JSON anahtarından (client_email, private_key). Geçersizse hata iletisi.</summary>
    public static (string? ClientEmail, string? PrivateKey, string? Error) ParseServiceAccount(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null, "Servis hesabı anahtarı (JSON) zorunlu.");
        try
        {
            var j = JsonDocument.Parse(json).RootElement;
            if (j.ValueKind != JsonValueKind.Object) return (null, null, "Servis hesabı anahtarı JSON nesnesi olmalı.");
            var type = j.TryGetProperty("type", out var t) ? t.GetString() : null;
            var email = j.TryGetProperty("client_email", out var e) ? e.GetString() : null;
            var key = j.TryGetProperty("private_key", out var k) ? k.GetString() : null;
            if (type != "service_account" || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
                return (null, null, "Anahtar dosyası bir Google servis hesabı anahtarı değil (type, client_email, private_key).");
            if (!key.Contains("PRIVATE KEY")) return (null, null, "private_key PEM biçiminde olmalı.");
            using var rsa = RSA.Create();
            try { rsa.ImportFromPem(key); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { return (null, null, "private_key okunamadı (PEM RSA anahtarı bekleniyor)."); }
            return (email.Trim(), key, null);
        }
        catch (JsonException) { return (null, null, "Servis hesabı anahtarı geçerli bir JSON değil."); }
    }

    /// <summary>RS256 imzalı JWT (Google OAuth 2.0 servis hesabı akışı, alan genelinde yetki: sub = yönetici).</summary>
    public static string ServiceAccountAssertion(string clientEmail, string privateKeyPem, string subject, string scope, string audience, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var iat = now.ToUnixTimeSeconds();
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = clientEmail, sub = subject, scope, aud = audience, iat, exp = iat + 3600 }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(sig)}";
    }

    /// <summary>Dört göz ve kendi hesabı kuralı. İzin varsa null, yoksa ileti.</summary>
    public static string? DecisionBlock(string source, string? requestedBy, string deciderUserId, Guid subjectEmployeeId, Guid? deciderEmployeeId)
    {
        if (deciderEmployeeId is { } me && me == subjectEmployeeId) return "Kendi hesabınızla ilgili isteğe karar veremezsiniz.";
        if (source == "Manual" && !string.IsNullOrEmpty(requestedBy) && requestedBy == deciderUserId)
            return "Elle açılan isteği açan kişi onaylayamaz (dört göz ilkesi); başka bir İK yöneticisi onaylamalı.";
        return null;
    }
}

/// <summary>
/// Google Workspace Admin SDK (Directory API). Servis hesabı + alan genelinde yetki (domain-wide delegation),
/// kapsam admin.directory.user; işlemler AdminSubject (süper yönetici) adına yapılır.
/// </summary>
public sealed class GoogleDirectoryProvisioner(IHttpClientFactory http) : IDirectoryProvisioner
{
    private static readonly string OAuthBase = EnvVar.Or("GOOGLE_OAUTH_BASE", "https://oauth2.googleapis.com").TrimEnd('/');
    private static readonly string AdminBase = EnvVar.Or("GOOGLE_ADMIN_BASE", "https://admin.googleapis.com").TrimEnd('/');
    public const string Scope = "https://www.googleapis.com/auth/admin.directory.user";
    private const string Audience = "https://oauth2.googleapis.com/token";
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Exp)> Tokens = new();
    public string Name => "Google";

    private async Task<string> TokenAsync(ProvisioningConfig cfg, CancellationToken ct)
    {
        var json = SecretBox.Unprotect(cfg.CredentialsEnc);
        var (email, key, error) = AccountNaming.ParseServiceAccount(json);
        if (error is not null) throw new ProviderApiException(error);
        if (string.IsNullOrWhiteSpace(cfg.AdminSubject)) throw new ProviderApiException("Yönetici hesabı (adına işlem yapılacak süper yönetici) zorunlu.");
        var cacheKey = $"{cfg.TenantSlug}:{cfg.AdminSubject}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cfg.CredentialsEnc ?? "")))[..16]}";
        if (Tokens.TryGetValue(cacheKey, out var c) && c.Exp > DateTime.UtcNow.AddMinutes(2)) return c.Token;
        var assertion = AccountNaming.ServiceAccountAssertion(email!, key!, cfg.AdminSubject!, Scope, Audience, DateTimeOffset.UtcNow);
        var j = await Http.SendAsync(http, HttpMethod.Post, $"{OAuthBase}/token", null, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion,
        }), ct, what: "Google servis hesabı jetonu");
        var t = Http.ToTokens(j);
        Tokens[cacheKey] = (t.AccessToken, DateTime.UtcNow.AddSeconds(t.ExpiresIn));
        return t.AccessToken;
    }

    public async Task TestAsync(ProvisioningConfig cfg, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        await Http.SendAsync(http, HttpMethod.Get, $"{AdminBase}/admin/directory/v1/users?domain={Uri.EscapeDataString(cfg.Domain)}&maxResults=1",
            token, null, ct, what: "Google Directory");
    }

    public async Task<string> CreateUserAsync(ProvisioningConfig cfg, NewAccount a, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        var body = new JsonObject
        {
            ["primaryEmail"] = a.PrimaryEmail,
            ["name"] = new JsonObject { ["givenName"] = a.GivenName, ["familyName"] = a.FamilyName },
            ["password"] = a.Password,
            ["changePasswordAtNextLogin"] = true,
        };
        if (!string.IsNullOrWhiteSpace(cfg.OrgUnit)) body["orgUnitPath"] = cfg.OrgUnit;
        try
        {
            var j = await Http.SendAsync(http, HttpMethod.Post, $"{AdminBase}/admin/directory/v1/users", token,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct, what: "Google hesap açma");
            return j is { } e && e.TryGetProperty("id", out var id) ? id.GetString() ?? a.PrimaryEmail : a.PrimaryEmail;
        }
        catch (ProviderApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
        {
            throw new ProviderApiException($"{a.PrimaryEmail} adresiyle bir hesap zaten var.", ex.Status);
        }
    }

    public async Task SuspendUserAsync(ProvisioningConfig cfg, string email, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        var key = Uri.EscapeDataString(email);
        await Http.SendAsync(http, HttpMethod.Put, $"{AdminBase}/admin/directory/v1/users/{key}", token,
            new StringContent("""{"suspended":true}""", Encoding.UTF8, "application/json"), ct, what: "Google hesabı askıya alma");
        // Açık oturumlar ve uygulama jetonları kapatılır.
        await Http.SendAsync(http, HttpMethod.Post, $"{AdminBase}/admin/directory/v1/users/{key}/signOut", token, null, ct, what: "Google oturum kapatma");
    }
}

/// <summary>Microsoft Graph (uygulama izni User.ReadWrite.All, istemci kimlik bilgileri akışı).</summary>
public sealed class MicrosoftDirectoryProvisioner(IHttpClientFactory http) : IDirectoryProvisioner
{
    private static readonly string Login = EnvVar.Or("MS_LOGIN_BASE", "https://login.microsoftonline.com").TrimEnd('/');
    private static readonly string Graph = EnvVar.Or("GRAPH_BASE", "https://graph.microsoft.com/v1.0").TrimEnd('/');
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Exp)> Tokens = new();
    public string Name => "Microsoft";

    private async Task<string> TokenAsync(ProvisioningConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.MsTenant) || string.IsNullOrWhiteSpace(cfg.ClientId))
            throw new ProviderApiException("Dizin (kiracı) kimliği ve uygulama kimliği zorunlu.");
        var cacheKey = $"{cfg.TenantSlug}:{cfg.MsTenant}:{cfg.ClientId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cfg.CredentialsEnc ?? "")))[..16]}";
        if (Tokens.TryGetValue(cacheKey, out var c) && c.Exp > DateTime.UtcNow.AddMinutes(2)) return c.Token;
        var j = await Http.SendAsync(http, HttpMethod.Post, $"{Login}/{Uri.EscapeDataString(cfg.MsTenant!)}/oauth2/v2.0/token", null,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId!, ["client_secret"] = SecretBox.Unprotect(cfg.CredentialsEnc) ?? "",
                ["grant_type"] = "client_credentials", ["scope"] = "https://graph.microsoft.com/.default",
            }), ct, what: "Microsoft uygulama jetonu");
        var t = Http.ToTokens(j);
        Tokens[cacheKey] = (t.AccessToken, DateTime.UtcNow.AddSeconds(t.ExpiresIn));
        return t.AccessToken;
    }

    public async Task TestAsync(ProvisioningConfig cfg, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        await Http.SendAsync(http, HttpMethod.Get, $"{Graph}/users?$top=1&$select=id", token, null, ct, what: "Microsoft Graph");
    }

    public async Task<string> CreateUserAsync(ProvisioningConfig cfg, NewAccount a, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        var body = new JsonObject
        {
            ["accountEnabled"] = true,
            ["displayName"] = $"{a.GivenName} {a.FamilyName}".Trim(),
            ["givenName"] = a.GivenName,
            ["surname"] = a.FamilyName,
            ["mailNickname"] = a.PrimaryEmail.Split('@')[0],
            ["userPrincipalName"] = a.PrimaryEmail,
            ["passwordProfile"] = new JsonObject { ["forceChangePasswordNextSignIn"] = true, ["password"] = a.Password },
        };
        if (!string.IsNullOrWhiteSpace(cfg.UsageLocation)) body["usageLocation"] = cfg.UsageLocation;
        try
        {
            var j = await Http.SendAsync(http, HttpMethod.Post, $"{Graph}/users", token,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct, what: "Microsoft hesap açma");
            return j is { } e && e.TryGetProperty("id", out var id) ? id.GetString() ?? a.PrimaryEmail : a.PrimaryEmail;
        }
        catch (ProviderApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict
            || (ex.Status == System.Net.HttpStatusCode.BadRequest && ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProviderApiException($"{a.PrimaryEmail} adresiyle bir hesap zaten var.", ex.Status);
        }
    }

    public async Task SuspendUserAsync(ProvisioningConfig cfg, string email, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        var key = Uri.EscapeDataString(email);
        await Http.SendAsync(http, HttpMethod.Patch, $"{Graph}/users/{key}", token,
            new StringContent("""{"accountEnabled":false}""", Encoding.UTF8, "application/json"), ct, what: "Microsoft hesabı kapatma");
        await Http.SendAsync(http, HttpMethod.Post, $"{Graph}/users/{key}/revokeSignInSessions", token, null, ct, what: "Microsoft oturum kapatma");
    }
}

/// <summary>Ayar ve istek tabloları (ham SQL; tablolar 2026-10-25_account_provisioning.sql ile gelir).</summary>
public static class ProvisioningStore
{
    private const string ConfigCols = """
        "Id","TenantSlug","Provider","IsEnabled","Domain","AutoCreate","AutoSuspend","ClientId","CredentialsEnc","AdminSubject",
        "OrgUnit","MsTenant","UsageLocation","LastTestAt","LastError","CreatedAt"
        """;

    private static ProvisioningConfig MapConfig(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6),
        r.Str(7), r.Str(8), r.Str(9), r.Str(10), r.Str(11), r.Str(12), r.Ts(13), r.Str(14), r.GetFieldValue<DateTime>(15));

    public static Task<List<ProvisioningConfig>> ConfigsAsync(Sql sql, string tenant, CancellationToken ct) =>
        sql.QueryAsync($"SELECT {ConfigCols} FROM governance_provisioning_configs WHERE \"TenantSlug\" = $1 ORDER BY \"Provider\"", MapConfig, ct, tenant);

    public static async Task<ProvisioningConfig?> ConfigAsync(Sql sql, string tenant, string provider, CancellationToken ct) =>
        (await sql.QueryAsync($"SELECT {ConfigCols} FROM governance_provisioning_configs WHERE \"TenantSlug\" = $1 AND \"Provider\" = $2",
            MapConfig, ct, tenant, provider)).FirstOrDefault();

    /// <summary>Etkin ayarlar (tüm kiracılar; arka plan taraması için).</summary>
    public static Task<List<ProvisioningConfig>> EnabledConfigsAsync(Sql sql, CancellationToken ct) =>
        sql.QueryAsync($"SELECT {ConfigCols} FROM governance_provisioning_configs WHERE \"IsEnabled\" ORDER BY \"TenantSlug\", \"Provider\"", MapConfig, ct);

    public sealed record Candidate(Guid EmployeeId, string FirstName, string LastName, string? Email, string? CreatedEmail);

    /// <summary>
    /// İşe girişi yakın (son 30 gün – önümüzdeki 60 gün), ayar açılmadan en fazla 30 gün önce kaydı açılmış,
    /// iş alan adında adresi olmayan ve bu sağlayıcı için hiç "Create" isteği bulunmayan aktif çalışanlar.
    /// </summary>
    public static Task<List<Candidate>> CreateCandidatesAsync(Sql sql, ProvisioningConfig cfg, CancellationToken ct) => sql.QueryAsync("""
        SELECT e."Id", e."FirstName", e."LastName", e."Email", NULL
        FROM employee_employees e
        WHERE e."TenantSlug" = $1 AND e."Status" = 'Active'
          AND e."HireDate" BETWEEN current_date - 30 AND current_date + 60
          AND e."CreatedAt" >= $3::timestamptz - interval '30 days'
          AND lower(coalesce(e."Email", '')) NOT LIKE '%@' || $4
          AND NOT EXISTS (SELECT 1 FROM governance_provisioning_requests q
                          WHERE q."TenantSlug" = $1 AND q."EmployeeId" = e."Id" AND q."Provider" = $2 AND q."Action" = 'Create')
        ORDER BY e."HireDate" LIMIT 200
        """, r => new Candidate(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Str(3), null), ct,
        cfg.TenantSlug, cfg.Provider, cfg.CreatedAt, cfg.Domain.ToLowerInvariant());

    /// <summary>
    /// Ayrılmış çalışanlar: bu sağlayıcıda HR360'ın açtığı hesabı olan ya da iş alan adında adresi olup ayrılışı
    /// (son görevlendirme bitişi) ayar açılmadan en fazla 7 gün önce olan; hiç "Suspend" isteği olmayanlar.
    /// </summary>
    public static Task<List<Candidate>> SuspendCandidatesAsync(Sql sql, ProvisioningConfig cfg, CancellationToken ct) => sql.QueryAsync("""
        SELECT e."Id", e."FirstName", e."LastName", e."Email", c."AccountEmail"
        FROM employee_employees e
        LEFT JOIN LATERAL (SELECT q."AccountEmail" FROM governance_provisioning_requests q
                           WHERE q."TenantSlug" = $1 AND q."EmployeeId" = e."Id" AND q."Provider" = $2 AND q."Action" = 'Create' AND q."Status" = 'Done'
                           ORDER BY q."CompletedAt" DESC NULLS LAST LIMIT 1) c ON true
        WHERE e."TenantSlug" = $1 AND e."Status" = 'Terminated'
          AND NOT EXISTS (SELECT 1 FROM governance_provisioning_requests q
                          WHERE q."TenantSlug" = $1 AND q."EmployeeId" = e."Id" AND q."Provider" = $2 AND q."Action" = 'Suspend')
          AND (c."AccountEmail" IS NOT NULL
               OR (lower(coalesce(e."Email", '')) LIKE '%@' || $4
                   AND EXISTS (SELECT 1 FROM employee_assignments a WHERE a."EmployeeId" = e."Id"
                                 AND a."EffectiveTo" >= ($3::timestamptz)::date - 7)))
        LIMIT 200
        """, r => new Candidate(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Str(3), r.Str(4)), ct,
        cfg.TenantSlug, cfg.Provider, cfg.CreatedAt, cfg.Domain.ToLowerInvariant());

    /// <summary>Yeni istek; aynı çalışan/sağlayıcı/işlem için açık ya da tamamlanmış istek varsa null (idempotent).</summary>
    public static async Task<Guid?> InsertRequestAsync(Sql sql, string tenant, Guid employeeId, string provider, string action, string source,
        string accountEmail, string? note, string? requestedBy, string? requestedByName, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var n = await sql.ExecuteAsync("""
            INSERT INTO governance_provisioning_requests ("Id","TenantSlug","EmployeeId","Provider","Action","Status","Source","AccountEmail","Note","RequestedBy","RequestedByName","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,'Pending',$6,$7,$8,$9,$10,now())
            ON CONFLICT ("TenantSlug","EmployeeId","Provider","Action") WHERE "Status" IN ('Pending','Processing','Done') DO NOTHING
            """, ct, id, tenant, employeeId, provider, action, source, accountEmail, note, requestedBy, requestedByName);
        return n == 1 ? id : null;
    }

    /// <summary>Kiracının etkin bir ayarı için tarama; açılan istek sayıları.</summary>
    public static async Task<(int Create, int Suspend)> ScanAsync(Sql sql, ProvisioningConfig cfg, CancellationToken ct)
    {
        int created = 0, suspended = 0;
        if (cfg.AutoCreate)
            foreach (var c in await CreateCandidatesAsync(sql, cfg, ct))
            {
                var email = AccountNaming.Propose(c.FirstName, c.LastName, cfg.Domain);
                if (email is null) continue;
                if (await InsertRequestAsync(sql, cfg.TenantSlug, c.EmployeeId, cfg.Provider, "Create", "Auto", email, null, null, "Sistem (işe giriş)", ct) is not null)
                    created++;
            }
        if (cfg.AutoSuspend)
            foreach (var c in await SuspendCandidatesAsync(sql, cfg, ct))
            {
                var email = c.CreatedEmail ?? c.Email;
                if (email is null || !AccountNaming.ValidAccountEmail(email, cfg.Domain)) continue;
                if (await InsertRequestAsync(sql, cfg.TenantSlug, c.EmployeeId, cfg.Provider, "Suspend", "Auto", email.ToLowerInvariant(), null, null, "Sistem (işten ayrılış)", ct) is not null)
                    suspended++;
            }
        return (created, suspended);
    }
}

/// <summary>
/// Arka plan taraması (PROVISIONING_SCAN_MINUTES, varsayılan 15). Yalnızca Pending istek açar; sağlayıcıya
/// çağrı İK onayıyla yapılır. Hatalar uyarı düzeyinde günlüğe yazılır (fail değil); tablo yoksa sessiz geçer.
/// </summary>
public sealed class ProvisioningScanner(Sql sql, ILogger<ProvisioningScanner> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(
        double.TryParse(EnvVar.Or("PROVISIONING_SCAN_MINUTES", "15"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Math.Clamp(m, 0.05, 1440) : 15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (ProvisioningFlag.Enabled)
            {
                try
                {
                    foreach (var cfg in await ProvisioningStore.EnabledConfigsAsync(sql, ct))
                    {
                        var (c, s) = await ProvisioningStore.ScanAsync(sql, cfg, ct);
                        if (c + s > 0)
                            log.LogInformation("Hesap sağlama taraması: kiracı {Tenant}, {Provider}: {Create} açma, {Suspend} askıya alma isteği onay bekliyor",
                                cfg.TenantSlug, cfg.Provider, c, s);
                    }
                }
                catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { /* migration uygulanmamış */ }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Hesap sağlama taraması tamamlanamadı: {Error}", ex.Message);
                }
            }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
