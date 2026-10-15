using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Directory;

/// <summary>
/// SCIM ve LDAP'in ortak saglama cekirdegi: Keycloak hesabi (kiraci organizasyonunda,
/// "employee" rolu), calisan kaydi (employee-service ic ucu, X-Internal-Token) ve dizin kaydi.
///
/// KVKK veri minimizasyonu: yalnizca kullanici adi, ad, soyad, birincil e-posta, unvan,
/// departman ve etkinlik durumu islenir. Rol atamasi dizinden YAPILMAZ: her saglanan hesap
/// yalnizca "employee" roluyle acilir; yonetici/IK yetkileri insan onayiyla Roller ekranindan.
///
/// Hesap kapatma (deprovision) SuspendUserForTenantAsync ile yapilir: hesap kapanir, tum
/// oturumlar sonlanir, ad/e-posta korunur (Keycloak 25'te kismi PUT bunlari siliyordu).
/// Calisan verisi SILINMEZ - isten cikis/saklama/imha surecini IK ve KVKK politikasi yonetir.
/// </summary>
public sealed class DirectoryProvisioningService
{
    private readonly TenantDbContext _db;
    private readonly KeycloakAdminClient _kc;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ILogger<DirectoryProvisioningService> _log;

    private readonly PasswordLinkService _links;

    public DirectoryProvisioningService(TenantDbContext db, KeycloakAdminClient kc, EmployeeDirectoryClient employees,
        ILogger<DirectoryProvisioningService> log, PasswordLinkService links)
    {
        _links = links;
        _db = db;
        _kc = kc;
        _employees = employees;
        _log = log;
    }

    public async Task<DirectorySettings> GetSettingsAsync(string slug, CancellationToken ct) =>
        await _db.DirectorySettings.FirstOrDefaultAsync(s => s.TenantSlug == slug, ct)
        ?? new DirectorySettings { TenantSlug = slug };

    private async Task EnsureUniqueAsync(string slug, ScimUserDraft d, Guid? self, string source, CancellationToken ct)
    {
        var userName = d.UserName!.ToLower();
        var email = d.Email!.ToLower();
        if (await _db.DirectoryUsers.AnyAsync(u => u.TenantSlug == slug && u.Id != self && u.UserName.ToLower() == userName, ct))
            throw ScimException.Uniqueness("Bu userName ile bir kullanıcı zaten var");
        if (await _db.DirectoryUsers.AnyAsync(u => u.TenantSlug == slug && u.Id != self && u.Email.ToLower() == email, ct))
            throw ScimException.Uniqueness("Bu e-posta ile bir kullanıcı zaten var");
        if (d.ExternalId is not null
            && await _db.DirectoryUsers.AnyAsync(u => u.TenantSlug == slug && u.Id != self && u.Source == source && u.ExternalId == d.ExternalId, ct))
            throw ScimException.Uniqueness("Bu externalId ile bir kullanıcı zaten var");
    }

    /// <summary>Dizindeki departman adini kiracinin organization-service departmanina esler (paylasilan veritabani, salt-okunur).</summary>
    private async Task<Guid?> ResolveDepartmentAsync(string slug, string? department, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(department)) return null;
        var name = department.Trim();
        var id = await _db.Database.SqlQuery<Guid>($@"SELECT ""Id"" AS ""Value"" FROM organization_departments
                    WHERE ""TenantSlug"" = {slug} AND lower(""Name"") = lower({name}) ORDER BY ""CreatedAt"" LIMIT 1").FirstOrDefaultAsync(ct);
        return id == Guid.Empty ? null : id;
    }

    /// <summary>
    /// Calisan kaydini olusturur/gunceller ve sonucu dizin satirina yazar (SaveChanges cagirani yapar).
    /// Hata saglamayi durdurmaz: satir "Failed" olur, neden EmployeeError'da gorunur, sonraki
    /// guncellemede/esitlemede yeniden denenir.
    /// </summary>
    private async Task SyncEmployeeAsync(DirectoryUser du, CancellationToken ct)
    {
        if (du.KeycloakUserId is null) return;
        var depId = await ResolveDepartmentAsync(du.TenantSlug, du.Department, ct);
        var r = await _employees.UpsertDirectoryEmployeeAsync(du.TenantSlug, du.KeycloakUserId, du.Email,
            du.GivenName ?? "", du.FamilyName ?? "", du.Title, depId, ct);
        if (r.EmployeeId is { } id)
        {
            du.EmployeeId = id;
            du.EmployeeState = EmployeeLinkStates.Linked;
            du.EmployeeError = depId is null && !string.IsNullOrWhiteSpace(du.Department)
                ? $"Departman eşleşmedi: {du.Department}" : null;
        }
        else
        {
            du.EmployeeState = EmployeeLinkStates.Failed;
            du.EmployeeError = r.Error is { Length: > 300 } e ? e[..300] : r.Error;
            _log.LogWarning("Dizin kullanicisi {Id} icin calisan kaydi olusturulamadi: {Error}", du.Id, du.EmployeeError);
        }
    }

    public async Task<DirectoryUser> CreateAsync(Tenant tenant, DirectorySettings settings, string source, ScimUserDraft d, CancellationToken ct)
    {
        if (tenant.KeycloakOrgId is null) throw new ScimException(500, null, "Kiracının Keycloak organizasyonu yok");
        await EnsureUniqueAsync(tenant.Slug, d, null, source, ct);

        var kcId = await _kc.FindUserByEmailAsync(d.Email!, ct);
        var created = false;
        if (kcId is not null)
        {
            // Invite akisindaki kuralla ayni: baska kiracinin/platformun hesabi asla devralinmaz.
            if (!await _kc.IsOrganizationMemberAsync(tenant.KeycloakOrgId, kcId, ct))
                throw ScimException.Uniqueness("Bu e-posta adresi başka bir hesapta kullanılıyor");
            if (await _db.DirectoryUsers.AnyAsync(u => u.TenantSlug == tenant.Slug && u.KeycloakUserId == kcId, ct))
                throw ScimException.Uniqueness("Bu hesap zaten dizinden yönetiliyor");
            // Yonetici hesaplari dizinden yonetime alinamaz (yanlislikla kapatilmasin).
            await EnsureNotProtectedAsync(tenant, kcId, ct);
        }
        else
        {
            kcId = await _kc.CreateUserAsync(d.Email!, d.GivenName, d.FamilyName, tenant.Id, ct);
            created = true;
            try { await _kc.AddOrganizationMemberAsync(tenant.KeycloakOrgId, kcId, ct); }
            catch
            {
                await _kc.DeleteUserAsync(kcId, ct);
                throw;
            }
        }
        await _kc.AssignRealmRoleAsync(kcId, "employee", ct);

        var du = new DirectoryUser
        {
            TenantSlug = tenant.Slug,
            Source = source,
            ExternalId = d.ExternalId,
            UserName = d.UserName!,
            GivenName = d.GivenName,
            FamilyName = d.FamilyName,
            Email = d.Email!,
            Title = d.Title,
            Department = d.Department,
            Active = true,
            KeycloakUserId = kcId,
        };
        _db.DirectoryUsers.Add(du);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            _db.Entry(du).State = EntityState.Detached;
            if (created) await _kc.DeleteUserAsync(kcId, ct);
            throw ScimException.Uniqueness("Kullanıcı aynı anda başka bir istekle oluşturuldu");
        }

        await SyncEmployeeAsync(du, ct);
        await _db.SaveChangesAsync(ct);

        if (!d.Active) await DeactivateAsync(tenant, du, ct);
        else if (created && settings.SendInvitations && !string.IsNullOrEmpty(d.Email))
        {
            // Davet: e-posta açıksa HR360 linki (ya da eski yöntemde Keycloak e-postası) gider; kapalıysa link
            // üretilmez, yönetici "Davetler" listesinden üretip iletir.
            try
            {
                await _links.IssueAsync(new PasswordLinkService.IssueRequest(tenant, kcId, d.Email,
                    $"{d.GivenName} {d.FamilyName}".Trim(), Models.PasswordInvitePurposes.Setup, LinkDisplay.Never,
                    new LinkActor(null, $"dizin:{source}")), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation("Dizin kullanicisina davet hazirlanamadi: {Message}", ex.Message);
            }
        }
        _log.LogInformation("Dizin saglamasi ({Source}): kiraci {Tenant}, kullanici {Id} {Mode}",
            source, tenant.Slug, du.Id, created ? "olusturuldu" : "mevcut hesap baglandi");
        return du;
    }

    public async Task UpdateAsync(Tenant tenant, DirectoryUser du, ScimUserDraft d, CancellationToken ct)
    {
        await EnsureUniqueAsync(tenant.Slug, d, du.Id, du.Source, ct);
        var emailChanged = !string.Equals(du.Email, d.Email, StringComparison.OrdinalIgnoreCase);
        var profileChanged = emailChanged || du.GivenName != d.GivenName || du.FamilyName != d.FamilyName;
        var employeeChanged = profileChanged || du.Title != d.Title || du.Department != d.Department
            || du.EmployeeState != EmployeeLinkStates.Linked;
        if (profileChanged && du.KeycloakUserId is not null)
        {
            if (emailChanged)
            {
                var other = await _kc.FindUserByEmailAsync(d.Email!, ct);
                if (other is not null && other != du.KeycloakUserId)
                    throw ScimException.Uniqueness("Bu e-posta adresi başka bir hesapta kullanılıyor");
            }
            await _kc.UpdateUserProfileAsync(du.KeycloakUserId, d.Email!, d.GivenName, d.FamilyName, ct);
        }

        du.UserName = d.UserName!;
        du.ExternalId = d.ExternalId;
        du.GivenName = d.GivenName;
        du.FamilyName = d.FamilyName;
        du.Email = d.Email!;
        du.Title = d.Title;
        du.Department = d.Department;
        du.UpdatedAt = DateTimeOffset.UtcNow;
        if (employeeChanged && d.Active) await SyncEmployeeAsync(du, ct);

        if (du.Active && !d.Active) await DeactivateAsync(tenant, du, ct);
        else if (!du.Active && d.Active) await ReactivateAsync(tenant, du, ct);
        else await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Kapatilamayacak hesaplar: kiracinin kurucu yoneticisi, tenant-admin ve platform-admin
    /// rolune sahip hesaplar (dizin hatasi sirketi yoneticisiz birakmasin).
    /// </summary>
    private async Task EnsureNotProtectedAsync(Tenant tenant, string keycloakUserId, CancellationToken ct)
    {
        if (string.Equals(tenant.AdminUserId, keycloakUserId, StringComparison.OrdinalIgnoreCase))
            throw new ScimException(403, null, "Şirketin kurucu yönetici hesabı dizin üzerinden yönetilemez");
        var roles = await _kc.GetUserRealmRolesAsync(keycloakUserId, ct);
        if (roles.Contains("tenant-admin") || roles.Contains("platform-admin"))
            throw new ScimException(403, null, "Yönetici hesapları dizin üzerinden yönetilemez");
    }

    /// <summary>Hesabi kapatir ve tum oturumlari sonlandirir (SuspendUserForTenantAsync: ad/e-posta korunur).</summary>
    public async Task DeactivateAsync(Tenant tenant, DirectoryUser du, CancellationToken ct)
    {
        if (du.KeycloakUserId is not null)
        {
            await EnsureNotProtectedAsync(tenant, du.KeycloakUserId, ct);
            await _kc.SuspendUserForTenantAsync(du.KeycloakUserId, ct);
        }
        du.Active = false;
        du.DeactivatedAt = DateTimeOffset.UtcNow;
        du.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReactivateAsync(Tenant tenant, DirectoryUser du, CancellationToken ct)
    {
        if (du.KeycloakUserId is not null)
        {
            await EnsureNotProtectedAsync(tenant, du.KeycloakUserId, ct);
            await _kc.EnableUserAsync(du.KeycloakUserId, ct);
        }
        du.Active = true;
        du.DeactivatedAt = null;
        du.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Calisan kaydi olusturulamamis etkin dizin kullanicilari icin yeniden dener.</summary>
    public async Task<(int Linked, int Failed)> RetryFailedAsync(string slug, CancellationToken ct, int max = 200)
    {
        var rows = await _db.DirectoryUsers
            .Where(u => u.TenantSlug == slug && u.Active && u.KeycloakUserId != null && u.EmployeeState != EmployeeLinkStates.Linked)
            .OrderBy(u => u.CreatedAt).Take(max).ToListAsync(ct);
        int linked = 0, failed = 0;
        foreach (var du in rows)
        {
            await SyncEmployeeAsync(du, ct);
            if (du.EmployeeState == EmployeeLinkStates.Linked) linked++; else failed++;
            du.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return (linked, failed);
    }

    public static ScimUserDraft ToDraft(DirectoryUser u) => new()
    {
        UserName = u.UserName,
        ExternalId = u.ExternalId,
        GivenName = u.GivenName,
        FamilyName = u.FamilyName,
        Email = u.Email,
        Title = u.Title,
        Department = u.Department,
        Active = u.Active,
    };
}
