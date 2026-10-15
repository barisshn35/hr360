using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Security;

namespace TenantService.Services;

/// <summary>
/// Guvenlik dalgasi 2A: sirketin iki adimli dogrulama politikasini Keycloak'ta uygular (bkz.
/// <see cref="MfaPolicyRules"/>). Zorunlu olup ikinci adimi (TOTP / passkey) olmayan kullaniciya
/// CONFIGURE_TOTP gerekli eylemi eklenir. Politika kapatildiginda eklenen eylemler kaldirilmaz
/// (kullanici kurulumu tamamlayabilir; yonetici isterse Keycloak'tan kaldirir).
/// </summary>
public sealed class MfaPolicyService
{
    private readonly TenantDbContext _db;
    private readonly KeycloakAdminClient _kc;
    private readonly SecurityNotifier _notifier;
    private readonly ILogger<MfaPolicyService> _log;

    public MfaPolicyService(TenantDbContext db, KeycloakAdminClient kc, SecurityNotifier notifier, ILogger<MfaPolicyService> log)
    {
        _db = db; _kc = kc; _notifier = notifier; _log = log;
    }

    public static bool PlatformAdminRequired =>
        (Environment.GetEnvironmentVariable("MFA_REQUIRE_PLATFORM_ADMIN") ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    public sealed record MemberMfa(string UserId, string? Username, bool HasOtp, bool HasPasskey, bool PendingSetup, bool Privileged, bool Required);

    /// <summary>Organizasyon uyelerinin iki adimli dogrulama durumu (en fazla 500 kullanici).</summary>
    public async Task<List<MemberMfa>> MembersAsync(string orgId, string? policy, CancellationToken ct)
    {
        var privileged = await PrivilegedUsersAsync(ct);
        var rows = new List<MemberMfa>();
        foreach (var id in (await _kc.ListOrganizationMemberIdsAsync(orgId, ct)).Take(500))
        {
            var st = await _kc.GetUserMfaStateAsync(id, ct);
            if (st is null) continue;
            var isPriv = privileged.Contains(id);
            rows.Add(new MemberMfa(id, st.Username,
                st.CredentialTypes.Contains("otp"),
                st.CredentialTypes.Any(t => t.StartsWith("webauthn", StringComparison.Ordinal)),
                st.RequiredActions.Contains(MfaPolicyRules.ConfigureTotp),
                isPriv,
                MfaPolicyRules.Requires(policy, isPriv ? MfaPolicyRules.PrivilegedRoles : Array.Empty<string>())));
        }
        return rows;
    }

    private async Task<HashSet<string>> PrivilegedUsersAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in MfaPolicyRules.PrivilegedRoles) set.UnionWith(await _notifier.RoleUsersAsync(role, ct));
        return set;
    }

    /// <summary>Sirketin politikasini uygular; CONFIGURE_TOTP eklenen kullanici sayisini doner.</summary>
    public async Task<int> ApplyTenantAsync(string tenantSlug, CancellationToken ct)
    {
        var t = await _db.Tenants.AsNoTracking().Where(x => x.Slug == tenantSlug)
            .Select(x => new { x.KeycloakOrgId, x.MfaPolicy }).FirstOrDefaultAsync(ct);
        if (t?.KeycloakOrgId is null || MfaPolicyRules.Normalize(t.MfaPolicy) == MfaPolicyRules.Off) return 0;
        var privileged = await PrivilegedUsersAsync(ct);
        var changed = 0;
        foreach (var id in await _kc.ListOrganizationMemberIdsAsync(t.KeycloakOrgId, ct))
        {
            var required = MfaPolicyRules.Requires(t.MfaPolicy, privileged.Contains(id) ? MfaPolicyRules.PrivilegedRoles : Array.Empty<string>());
            if (await EnsureAsync(id, required, ct)) changed++;
        }
        if (changed > 0) _log.LogInformation("İki adımlı doğrulama politikası uygulandı ({Tenant}): {Count} kullanıcıya kurulum zorunluluğu", tenantSlug, changed);
        return changed;
    }

    /// <summary>MFA_REQUIRE_PLATFORM_ADMIN=true ise platform yoneticilerine zorunluluk.</summary>
    public async Task<int> ApplyPlatformAdminsAsync(CancellationToken ct)
    {
        if (!PlatformAdminRequired) return 0;
        var changed = 0;
        foreach (var id in await _kc.ListRoleUserIdsAsync("platform-admin", ct))
            if (await EnsureAsync(id, true, ct)) changed++;
        return changed;
    }

    private async Task<bool> EnsureAsync(string userId, bool required, CancellationToken ct)
    {
        if (!required) return false;
        var st = await _kc.GetUserMfaStateAsync(userId, ct);
        if (st is null || !MfaPolicyRules.NeedsSetup(true, st.CredentialTypes, st.RequiredActions)) return false;
        await _kc.RequireOtpSetupAsync(userId, ct);
        return true;
    }
}

/// <summary>
/// Iki adimli dogrulama politikasini duzenli olarak (MFA_POLICY_INTERVAL_SECONDS, varsayilan
/// 600 sn; 0 = kapali) uygular: sonradan yetkili role atanan ya da dogrulayicisini silen
/// kullanicilar da kapsanir.
/// </summary>
public sealed class MfaPolicyHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MfaPolicyHostedService> _log;
    public MfaPolicyHostedService(IServiceScopeFactory scopes, ILogger<MfaPolicyHostedService> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("MFA_POLICY_INTERVAL_SECONDS"), out var s) ? s : 600;
        if (seconds <= 0) return;
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                var svc = scope.ServiceProvider.GetRequiredService<MfaPolicyService>();
                var slugs = await db.Tenants.AsNoTracking()
                    .Where(t => t.MfaPolicy != null && t.MfaPolicy != MfaPolicyRules.Off && t.KeycloakOrgId != null)
                    .Select(t => t.Slug).ToListAsync(ct);
                foreach (var slug in slugs) await svc.ApplyTenantAsync(slug, ct);
                await svc.ApplyPlatformAdminsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("İki adımlı doğrulama politikası uygulanamadı: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
