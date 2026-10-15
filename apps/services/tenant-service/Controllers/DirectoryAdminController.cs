using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Directory;
using TenantService.Models;
using TenantService.Security;

namespace TenantService.Controllers;

/// <summary>
/// Y26: dizin saglama yonetimi (Ayarlar › Güvenlik › SCIM / LDAP-Active Directory). SCIM
/// jetonlari (yalnizca SHA-256 ozeti saklanir, bir kez gosterilir), LDAP/AD ayarlari (bağlama
/// parolasi AES-256-GCM ile sifreli), baglanti testi, esitleme (dry-run / simdi) ve dizinden
/// gelen kullanicilarin calisan kaydi durumu. Degisiklikler yalnizca tenant-admin'e acik.
/// </summary>
[ApiController]
[Route("api/my-tenant/directory")]
[Authorize]
public class DirectoryAdminController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly ScimTokenService _tokens;
    private readonly DirectoryProvisioningService _prov;
    private readonly DirectorySyncService _sync;
    private readonly SmtpCredentialProtector _protector;

    public DirectoryAdminController(TenantDbContext db, ScimTokenService tokens, DirectoryProvisioningService prov,
        DirectorySyncService sync, SmtpCredentialProtector protector)
    {
        _db = db;
        _tokens = tokens;
        _prov = prov;
        _sync = sync;
        _protector = protector;
    }

    private async Task<Tenant?> MyTenantAsync(CancellationToken ct)
    {
        var slug = OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        return string.IsNullOrWhiteSpace(slug) ? null : await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
    }

    private IActionResult NoTenant() => NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

    private string? Me => User.FindFirst("preferred_username")?.Value ?? User.FindFirst("email")?.Value;

    /* ------------------------------------------------------------ ayarlar */

    private static object SettingsView(DirectorySettings s)
    {
        object? summary = null;
        if (!string.IsNullOrEmpty(s.LastSyncSummary))
            try { summary = JsonDocument.Parse(s.LastSyncSummary).RootElement.Clone(); } catch (JsonException) { }
        string? urlWarning = null;
        if (!string.IsNullOrWhiteSpace(s.LdapUrl)) urlWarning = LdapUrl.Parse(s.LdapUrl, LdapUrl.AllowPrivateHostsFromEnv()).Warning;
        return new
        {
            s.SendInvitations,
            dataMinimisation = ScimUserMapper.MinimisationNoticeTr,
            storedAttributes = ScimUserMapper.StoredAttributes,
            scimBaseUrl = (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "").TrimEnd('/') + "/api/tenant/scim/v2",
            ldap = new
            {
                enabled = s.LdapEnabled, autoSync = s.LdapAutoSync, url = s.LdapUrl, bindDn = s.LdapBindDn,
                hasPassword = s.LdapBindPasswordEncrypted is not null, baseDn = s.LdapBaseDn, userFilter = s.LdapUserFilter,
                usernameAttr = s.LdapUsernameAttr, emailAttr = s.LdapEmailAttr, departmentAttr = s.LdapDepartmentAttr,
                titleAttr = s.LdapTitleAttr, disabledAttr = s.LdapDisabledAttr, urlWarning,
                lastSyncAt = s.LastSyncAt, lastSyncTrigger = s.LastSyncTrigger, lastSyncStatus = s.LastSyncStatus, lastSyncSummary = summary,
            },
            syncIntervalMinutes = DirectorySyncHostedService.Interval.TotalMinutes,
            allowed = new
            {
                username = LdapAttributeMap.AllowedUsername, email = LdapAttributeMap.AllowedEmail,
                department = LdapAttributeMap.AllowedDepartment, disabled = LdapAttributeMap.AllowedDisabled,
                title = LdapAttributeMap.AllowedTitle,
            },
        };
    }

    [HttpGet("settings")]
    [Authorize(Policy = "RequireTenantAdmin")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        return Ok(SettingsView(await _prov.GetSettingsAsync(tenant.Slug, ct)));
    }

    public record LdapSettingsInput(
        bool Enabled, bool AutoSync, string? Url, string? BindDn, string? BindPassword, string? BaseDn, string? UserFilter,
        string? UsernameAttr, string? EmailAttr, string? DepartmentAttr, string? DisabledAttr, string? TitleAttr);

    public record SettingsInput(bool? SendInvitations, LdapSettingsInput? Ldap);

    [HttpPut("settings")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> PutSettings([FromBody] SettingsInput body, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var s = await _prov.GetSettingsAsync(tenant.Slug, ct);
        if (_db.Entry(s).State == EntityState.Detached) _db.DirectorySettings.Add(s);

        if (body.SendInvitations is { } inv) s.SendInvitations = inv;

        string? warning = null;
        if (body.Ldap is { } l)
        {
            static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            var map = new LdapAttributeMap(Blank(l.UsernameAttr) ?? "uid", Blank(l.EmailAttr) ?? "mail", Blank(l.DepartmentAttr),
                Blank(l.DisabledAttr), Blank(l.TitleAttr));
            if (map.Validate() is { } merr) return BadRequest(new { message = merr });
            var url = Blank(l.Url);
            if (url is not null)
            {
                var parsed = LdapUrl.Parse(url, LdapUrl.AllowPrivateHostsFromEnv());
                if (parsed.Error is not null) return BadRequest(new { message = parsed.Error });
                warning = parsed.Warning;
            }
            var bindDn = Blank(l.BindDn);
            var baseDn = Blank(l.BaseDn);
            var filter = Blank(l.UserFilter);
            if (l.Enabled || url is not null)
            {
                if (url is null) return BadRequest(new { message = "LDAP adresi zorunlu" });
                if (LdapDirectoryReader.ValidateFilterAndDn(baseDn, filter, bindDn) is { } err) return BadRequest(new { message = err });
            }
            if (l.BindPassword is not null)
                s.LdapBindPasswordEncrypted = l.BindPassword.Length == 0 ? null : _protector.Encrypt(l.BindPassword);
            if (l.Enabled && s.LdapBindPasswordEncrypted is null)
                return BadRequest(new { message = "LDAP bağlama parolası zorunlu" });
            s.LdapEnabled = l.Enabled;
            s.LdapAutoSync = l.Enabled && l.AutoSync;
            s.LdapUrl = url;
            s.LdapBindDn = bindDn;
            s.LdapBaseDn = baseDn;
            s.LdapUserFilter = filter;
            s.LdapUsernameAttr = map.UsernameAttr;
            s.LdapEmailAttr = map.EmailAttr;
            s.LdapDepartmentAttr = map.DepartmentAttr;
            s.LdapTitleAttr = map.TitleAttr;
            s.LdapDisabledAttr = map.DisabledAttr;
        }
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Dizin ayarları kaydedildi", warning, settings = SettingsView(s) });
    }

    /* ------------------------------------------------------------ SCIM jetonlari */

    [HttpGet("scim-tokens")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> ListTokens(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var rows = await _db.ScimTokens.Where(t => t.TenantSlug == tenant.Slug)
            .OrderByDescending(t => t.CreatedAt).Take(50)
            .Select(t => new { t.Id, t.Name, t.TokenPrefix, t.CreatedAt, t.CreatedBy, t.LastUsedAt, t.RevokedAt, active = t.RevokedAt == null })
            .ToListAsync(ct);
        return Ok(rows);
    }

    public record TokenInput(string? Name);

    [HttpPost("scim-tokens")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> CreateToken([FromBody] TokenInput body, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var name = (body.Name ?? "").Trim();
        if (name.Length is 0 or > 100) return BadRequest(new { message = "Jeton adı 1-100 karakter olmalı (örn. Entra ID)" });
        var active = await _db.ScimTokens.CountAsync(t => t.TenantSlug == tenant.Slug && t.RevokedAt == null, ct);
        if (active >= ScimTokenService.MaxActivePerTenant)
            return Conflict(new { message = $"En fazla {ScimTokenService.MaxActivePerTenant} etkin SCIM jetonu olabilir; kullanılmayanı iptal edin" });
        var (row, token) = await _tokens.CreateAsync(tenant.Slug, name, Me, ct);
        return Ok(new
        {
            row.Id, row.Name, row.TokenPrefix, row.CreatedAt, token,
            message = "Jeton yalnızca şimdi gösteriliyor; güvenli bir yere kopyalayın.",
        });
    }

    [HttpPost("scim-tokens/{id:guid}/rotate")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> RotateToken(Guid id, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var old = await _db.ScimTokens.FirstOrDefaultAsync(t => t.Id == id && t.TenantSlug == tenant.Slug && t.RevokedAt == null, ct);
        if (old is null) return NotFound(new { message = "Etkin jeton bulunamadı" });
        old.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var (row, token) = await _tokens.CreateAsync(tenant.Slug, old.Name, Me, ct);
        return Ok(new
        {
            row.Id, row.Name, row.TokenPrefix, row.CreatedAt, token, revokedId = old.Id,
            message = "Eski jeton iptal edildi. Yeni jeton yalnızca şimdi gösteriliyor.",
        });
    }

    [HttpDelete("scim-tokens/{id:guid}")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var row = await _db.ScimTokens.FirstOrDefaultAsync(t => t.Id == id && t.TenantSlug == tenant.Slug, ct);
        if (row is null) return NotFound(new { message = "Jeton bulunamadı" });
        if (row.RevokedAt is null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { message = "Jeton iptal edildi" });
    }

    /* ------------------------------------------------------------ LDAP */

    [HttpPost("ldap/test")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> TestLdap(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var s = await _prov.GetSettingsAsync(tenant.Slug, ct);
        try
        {
            var (count, sample, warning) = await _sync.TestAsync(s, ct);
            return Ok(new { ok = true, entries = count, sample, warning });
        }
        catch (DirectorySyncService.SyncFailedException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
    }

    public record SyncInput(bool DryRun, bool ForceDisable);

    /// <summary>
    /// Elle esitleme. dryRun=true: yalnizca plan/rapor (hicbir sey degismez). Gercek esitlemede
    /// dizinde olup HR360'ta olmayanlar olusturulur (Keycloak + calisan kaydi), dizinden silinen
    /// ya da pasiflestirilenler kapatilir (toplu kapatma korumasi: forceDisable).
    /// </summary>
    [HttpPost("ldap/sync")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> SyncLdap([FromBody] SyncInput body, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        if (tenant.Status != TenantStatus.Active) return Conflict(new { message = "Şirket etkin değil; eşitleme yapılamaz" });
        var s = await _prov.GetSettingsAsync(tenant.Slug, ct);
        if (!s.LdapEnabled) return BadRequest(new { message = "Önce LDAP eşitlemesini etkinleştirip ayarları kaydedin" });
        try
        {
            var r = await _sync.RunAsync(tenant, body.DryRun ? "dry-run" : "manual", body.DryRun, body.ForceDisable, ct);
            return Ok(r);
        }
        catch (DirectorySyncService.SyncFailedException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /* ------------------------------------------------------------ dizin kullanicilari */

    [HttpGet("users")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ListUsers([FromQuery] string? source, [FromQuery] string? state, CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var q = _db.DirectoryUsers.Where(u => u.TenantSlug == tenant.Slug);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(u => u.Source == source);
        if (!string.IsNullOrWhiteSpace(state)) q = q.Where(u => u.EmployeeState == state);
        var rows = await q.OrderByDescending(u => u.UpdatedAt).Take(500).ToListAsync(ct);
        return Ok(new
        {
            failed = await _db.DirectoryUsers.CountAsync(u => u.TenantSlug == tenant.Slug && u.Active
                && u.EmployeeState != EmployeeLinkStates.Linked, ct),
            items = rows.Select(u => new
            {
                u.Id, u.Source, u.UserName, u.GivenName, u.FamilyName, u.Email, u.Title, u.Department, u.Active,
                u.EmployeeId, u.EmployeeState, u.EmployeeError, u.CreatedAt, u.UpdatedAt, u.DeactivatedAt,
            }),
        });
    }

    /// <summary>Calisan kaydi olusturulamamis dizin kullanicilari icin yeniden dener.</summary>
    [HttpPost("users/retry")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> RetryEmployees(CancellationToken ct)
    {
        var tenant = await MyTenantAsync(ct);
        if (tenant is null) return NoTenant();
        var (linked, failed) = await _prov.RetryFailedAsync(tenant.Slug, ct);
        return Ok(new { linked, failed });
    }
}
