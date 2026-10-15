using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TenantService.Security;

namespace TenantService.Services;

/// <summary>
/// Guvenlik dalgasi 2A: supheli giris tespiti. Keycloak'in hr360 realm olay deposundan
/// (yalnizca LOGIN ve LOGIN_ERROR, 30 gun) LOGIN_WATCH_INTERVAL_SECONDS (varsayilan 60; 0 =
/// kapali) aralikla yeni olaylari okur ve <see cref="LoginWatchRules"/> kurallarini uygular:
///
///   * Basarili giris, kullanicinin kayitli aglari disindan -> kullaniciya "yeni bir agdan
///     giris" bildirimi + tenant_security_alerts. Ilk gozlem yalnizca kaydedilir.
///   * 10 dakikada 5+ hatali giris (ayni kullanici ya da ayni ag) -> sirketin IK ve sirket
///     yoneticilerine bildirim, audit_log ("SuspiciousLogin") ve tenant_security_alerts.
///     Hicbir sirkete baglanamayan ag uyarisi (olmayan kullanici adlari) yalnizca audit_log'a
///     (TenantSlug bos) ve servis gunlugune yazilir.
///
/// KVKK: ham IP veritabanina, bildirime ve gunluge yazilmaz (ag oneki anahtarli ozet); olay
/// deposundaki ayrintilar Keycloak'ta 30 gun sonra silinir. Durum (bilinen aglar) 180 gun
/// kullanilmazsa silinir. Servis ilk acildiginda son 5 dakikadan baslar (gecmis taranmaz).
/// </summary>
public sealed class LoginWatchHostedService : BackgroundService
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("TENANT_DB_CONNECTION");

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LoginWatchHostedService> _log;
    private readonly byte[] _key;
    private readonly FailureCounter _userFailures = new(LoginWatchRules.FailureThreshold, LoginWatchRules.FailureWindow, LoginWatchRules.AlertCooldown);
    private readonly FailureCounter _networkFailures = new(LoginWatchRules.FailureThreshold, LoginWatchRules.FailureWindow, LoginWatchRules.AlertCooldown);
    // Ag basina pencere icindeki kullanicilar: ag uyarisinin hangi sirketlere gidecegi.
    private readonly Dictionary<string, List<(DateTimeOffset At, string? UserId)>> _networkUsers = new(StringComparer.Ordinal);
    private long _cursor;
    private readonly HashSet<string> _seenAtCursor = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public LoginWatchHostedService(IServiceScopeFactory scopes, ILogger<LoginWatchHostedService> log)
    {
        _scopes = scopes; _log = log;
        // Ag ozeti anahtari: TENANT_SECRET_KEY'den turetilir (veritabanina girmez). Anahtar
        // degisirse kullanicilarin bilinen aglari bir kez "ilk gozlem" gibi yeniden ogrenilir.
        var secret = Environment.GetEnvironmentVariable("LOGIN_WATCH_KEY")
            ?? Environment.GetEnvironmentVariable("TENANT_SECRET_KEY") ?? "hr360-login-watch";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("hr360-login-watch|" + secret));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("LOGIN_WATCH_INTERVAL_SECONDS"), out var s) ? s : 60;
        if (seconds <= 0 || string.IsNullOrEmpty(Cs)) return;
        _cursor = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct);
                warned = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Olay deposu kapali / yetki yok: her turda degil, durum degisiminde uyar.
                if (!warned) _log.LogWarning("Giriş olayları okunamadı (şüpheli giriş tespiti bekliyor): {Message}", ex.Message);
                warned = true;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static string EventKey(KeycloakAdminClient.LoginEvent e) => $"{e.Time}|{e.Type}|{e.UserId}|{e.IpAddress}|{e.Error}";

    private async Task PollAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var kc = scope.ServiceProvider.GetRequiredService<KeycloakAdminClient>();
        var notifier = scope.ServiceProvider.GetRequiredService<SecurityNotifier>();

        // Olaylar yeniden eskiye gelir; imlece ulasana kadar sayfalanir (tur basina en fazla 5000).
        var from = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(_cursor).UtcDateTime);
        var fresh = new List<KeycloakAdminClient.LoginEvent>();
        // Sayfalama sirasinda gelen yeni olaylar kaydirma yapar; ayni olay iki kez islenmesin.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        const int page = 500;
        for (var first = 0; first < 5000; first += page)
        {
            var batch = await kc.ListLoginEventsAsync(from, first, page, ct);
            var reached = false;
            foreach (var e in batch)
            {
                if (e.Time < _cursor || (e.Time == _cursor && _seenAtCursor.Contains(EventKey(e)))) { reached = true; break; }
                if (keys.Add(EventKey(e))) fresh.Add(e);
            }
            if (reached || batch.Count < page) break;
        }
        if (fresh.Count == 0) return;
        fresh.Reverse();
        foreach (var e in fresh)
        {
            await HandleAsync(e, notifier, ct);
            if (e.Time > _cursor) { _cursor = e.Time; _seenAtCursor.Clear(); }
            _seenAtCursor.Add(EventKey(e));
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastPrune > TimeSpan.FromHours(6))
        {
            _lastPrune = now;
            _userFailures.Prune(now);
            _networkFailures.Prune(now);
            foreach (var k in _networkUsers.Where(kv => kv.Value.All(x => now - x.At > LoginWatchRules.FailureWindow)).Select(kv => kv.Key).ToList())
                _networkUsers.Remove(k);
            await PruneAsync(ct);
        }
    }

    private async Task HandleAsync(KeycloakAdminClient.LoginEvent e, SecurityNotifier notifier, CancellationToken ct)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(e.Time);
        var net = LoginWatchRules.NetworkHash(e.IpAddress, _key);
        if (e.Type == "LOGIN" && e.UserId is not null && net is not null)
        {
            var tenant = await notifier.TenantOfUserAsync(e.UserId, ct);
            if (tenant is null) return; // platform hesabi / kiraciya bagli olmayan kullanici
            var (known, isKnown) = await RememberNetworkAsync(tenant, e.UserId, net, at, ct);
            if (!LoginWatchRules.IsNewNetworkAlert(known, isKnown)) return;
            await AlertAsync(tenant, "new_network", e.UserId, e.Username, net, 1, ct);
            var recipients = await notifier.EmployeesOfUsersAsync(tenant, new[] { e.UserId }, ct);
            await notifier.NotifyAsync(tenant, recipients, "Yeni bir ağdan giriş",
                $"Hesabınıza daha önce kullanmadığınız bir ağdan giriş yapıldı ({at.ToOffset(TimeSpan.FromHours(3)):dd.MM.yyyy HH:mm}). Bu siz değilseniz parolanızı değiştirin ve Profil › Oturumlarım'dan diğer oturumları kapatın.",
                "security.new_login", ct);
            return;
        }

        if (!LoginWatchRules.IsCountedFailure(e.Type, e.Error)) return;

        if (e.UserId is not null)
        {
            var key = "u:" + e.UserId;
            var n = _userFailures.Add(key, at);
            if (_userFailures.ShouldAlert(key, n, at))
            {
                var tenant = await notifier.TenantOfUserAsync(e.UserId, ct);
                if (tenant is not null && !await RecentAlertAsync(tenant, "failed_user", e.UserId, null, ct))
                    await FailedLoginAlertAsync(notifier, tenant, "failed_user", e.UserId, e.Username, null, n, ct);
            }
        }

        if (net is not null)
        {
            if (!_networkUsers.TryGetValue(net, out var users)) _networkUsers[net] = users = new();
            users.Add((at, e.UserId));
            users.RemoveAll(x => at - x.At > LoginWatchRules.FailureWindow);
            var key = "n:" + net;
            var n = _networkFailures.Add(key, at);
            if (_networkFailures.ShouldAlert(key, n, at))
            {
                var tenants = new HashSet<string>(StringComparer.Ordinal);
                foreach (var uid in users.Select(x => x.UserId).Where(x => x is not null).Distinct())
                    if (await notifier.TenantOfUserAsync(uid!, ct) is { } t) tenants.Add(t);
                if (tenants.Count == 0)
                {
                    _log.LogWarning("Şüpheli giriş: tek ağdan {Count} hatalı giriş (10 dk), hiçbir şirkete bağlanamadı", n);
                    await notifier.AuditAsync(null, "LoginNetwork", net, "SuspiciousLogin",
                        new { kind = "failed_network", failures = n, windowMinutes = 10 }, null, null, ct);
                }
                foreach (var t in tenants)
                    if (!await RecentAlertAsync(t, "failed_network", null, net, ct))
                        await FailedLoginAlertAsync(notifier, t, "failed_network", null, null, net, n, ct);
            }
        }
    }

    private async Task FailedLoginAlertAsync(SecurityNotifier notifier, string tenant, string kind, string? userId, string? username,
        string? net, int count, CancellationToken ct)
    {
        await AlertAsync(tenant, kind, userId, username, net, count, ct);
        await notifier.AuditAsync(tenant, kind == "failed_user" ? "LoginUser" : "LoginNetwork", userId ?? net ?? "", "SuspiciousLogin",
            new { kind, failures = count, windowMinutes = 10, username }, null, null, ct);
        var admins = await notifier.TenantUsersWithRolesAsync(tenant, new[] { "hr-admin", "tenant-admin" }, ct);
        var recipients = await notifier.EmployeesOfUsersAsync(tenant, admins, ct);
        var body = kind == "failed_user"
            ? $"{username ?? "Bir kullanıcı"} hesabına son 10 dakikada {count} hatalı giriş denemesi yapıldı. Hesap sahibine ulaşın; gerekirse Güvenlik ekranından oturumlarını kapatın."
            : $"Şirket hesaplarına aynı ağdan son 10 dakikada {count} hatalı giriş denemesi yapıldı. Güvenlik ekranındaki giriş uyarılarını inceleyin.";
        await notifier.NotifyAsync(tenant, recipients, "Şüpheli giriş denemeleri", body, "security.failed_logins", ct);
    }

    /// <summary>Agi kaydeder/gunceller; (kayitli ag sayisi [bu girisi saymadan], ag zaten biliniyor muydu) doner.</summary>
    private static async Task<(int Known, bool IsKnown)> RememberNetworkAsync(string tenant, string userId, string net, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH prev AS (
                SELECT count(*) AS n, bool_or("NetworkHash" = @h) AS known
                FROM tenant_login_networks WHERE "TenantSlug" = @t AND "UserId" = @u
            ), up AS (
                INSERT INTO tenant_login_networks ("Id","TenantSlug","UserId","NetworkHash","FirstSeenAt","LastSeenAt")
                VALUES (gen_random_uuid(), @t, @u, @h, @at, @at)
                ON CONFLICT ("TenantSlug","UserId","NetworkHash") DO UPDATE SET "LastSeenAt" = GREATEST(tenant_login_networks."LastSeenAt", EXCLUDED."LastSeenAt")
            )
            SELECT n, coalesce(known, false) FROM prev
            """, conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("h", net);
        cmd.Parameters.AddWithValue("at", at);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ((int)r.GetInt64(0), r.GetBoolean(1));
    }

    private static async Task AlertAsync(string tenant, string kind, string? userId, string? username, string? net, int count, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO tenant_security_alerts ("Id","TenantSlug","Kind","UserId","Username","NetworkHash","Count","CreatedAt")
            VALUES (gen_random_uuid(), @t, @k, @u, @n, @h, @c, now())
            """, conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("u", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("n", (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("h", (object?)net ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", count);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ayni konu icin son 60 dakikada uyari var mi (servis yeniden baslasa da tekrar etmesin)?</summary>
    private static async Task<bool> RecentAlertAsync(string tenant, string kind, string? userId, string? net, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM tenant_security_alerts WHERE "TenantSlug" = @t AND "Kind" = @k
              AND "UserId" IS NOT DISTINCT FROM @u AND "NetworkHash" IS NOT DISTINCT FROM @h
              AND "CreatedAt" > now() - interval '60 minutes' LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)net ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>KVKK saklama: 180 gun kullanilmayan ag kayitlari ve eski uyarilar silinir.</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                DELETE FROM tenant_login_networks WHERE "LastSeenAt" < now() - interval '180 days';
                DELETE FROM tenant_security_alerts WHERE "CreatedAt" < now() - interval '180 days';
                """, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Giriş izleme kayıtları temizlenemedi: {Message}", ex.Message);
        }
    }
}
