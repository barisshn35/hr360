using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Models;
using TenantService.Security;

namespace TenantService.Directory;

/// <summary>LDAP/AD esitlemesi: ara, esle, planla (dry-run) ve uygula.</summary>
public sealed class DirectorySyncService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly TenantDbContext _db;
    private readonly DirectoryProvisioningService _prov;
    private readonly LdapDirectoryReader _reader;
    private readonly SmtpCredentialProtector _protector;
    private readonly ILogger<DirectorySyncService> _log;

    public DirectorySyncService(TenantDbContext db, DirectoryProvisioningService prov,
        LdapDirectoryReader reader, SmtpCredentialProtector protector, ILogger<DirectorySyncService> log)
    {
        _db = db;
        _prov = prov;
        _reader = reader;
        _protector = protector;
        _log = log;
    }

    public sealed class SyncFailedException : Exception
    {
        public SyncFailedException(string message) : base(message) { }
    }

    public sealed record SyncResult(
        bool DryRun, string? Warning, string? ConnectionWarning, int Entries,
        int Created, int Updated, int Disabled, int Enabled, int Skipped, int Unchanged,
        List<object> Actions, List<string> Errors, int EmployeeRetried, int EmployeeFailed);

    public static LdapAttributeMap MapFor(DirectorySettings s) =>
        new(s.LdapUsernameAttr, s.LdapEmailAttr, string.IsNullOrWhiteSpace(s.LdapDepartmentAttr) ? null : s.LdapDepartmentAttr,
            string.IsNullOrWhiteSpace(s.LdapDisabledAttr) ? null : s.LdapDisabledAttr,
            string.IsNullOrWhiteSpace(s.LdapTitleAttr) ? null : s.LdapTitleAttr);

    /// <summary>Yalnizca baglanti + arama (kayit sayisi ve ilk birkac kullanici adi).</summary>
    public async Task<(int Count, List<string> Sample, string? Warning)> TestAsync(DirectorySettings s, CancellationToken ct)
    {
        var (entries, warning) = await ReadAsync(s, ct);
        var map = MapFor(s);
        return (entries.Count, entries.Take(5).Select(e => LdapMapper.Map(e, map).UserName).ToList(), warning);
    }

    private async Task<(List<LdapEntry> Entries, string? Warning)> ReadAsync(DirectorySettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.LdapUrl) || string.IsNullOrWhiteSpace(s.LdapBindPasswordEncrypted))
            throw new SyncFailedException("LDAP ayarları eksik (adres, bağlama DN'i ve parola zorunlu)");
        if (LdapDirectoryReader.ValidateFilterAndDn(s.LdapBaseDn, s.LdapUserFilter, s.LdapBindDn) is { } err) throw new SyncFailedException(err);
        var map = MapFor(s);
        if (map.Validate() is { } merr) throw new SyncFailedException(merr);
        var (target, terr) = await LdapUrl.ResolveAsync(s.LdapUrl, ct);
        if (target is null) throw new SyncFailedException(terr!);
        string password;
        try { password = _protector.Decrypt(s.LdapBindPasswordEncrypted); }
        catch (Exception) { throw new SyncFailedException("Kayıtlı LDAP parolası çözülemedi; parolayı yeniden girin"); }
        var filter = string.IsNullOrWhiteSpace(s.LdapUserFilter) ? "(objectClass=person)" : s.LdapUserFilter.Trim();
        try
        {
            var entries = await _reader.SearchAsync(target, s.LdapBindDn!, password, s.LdapBaseDn!, filter, map.RequestedAttributes(), ct);
            return (entries, target.Warning);
        }
        catch (LdapDirectoryReader.LdapReadException ex) { throw new SyncFailedException(ex.Message); }
    }

    public async Task<SyncResult> RunAsync(Tenant tenant, string trigger, bool dryRun, bool forceDisable, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(tenant.Slug, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new SyncFailedException("Bu şirket için bir dizin eşitlemesi zaten sürüyor");
        var settings = await _prov.GetSettingsAsync(tenant.Slug, ct);
        try
        {
            var (entries, connWarning) = await ReadAsync(settings, ct);
            var map = MapFor(settings);
            var mapped = entries.Select(e => LdapMapper.Map(e, map)).ToList();
            var existingRows = await _db.DirectoryUsers.AsNoTracking().Where(u => u.TenantSlug == tenant.Slug && u.Source == DirectorySources.Ldap).ToListAsync(ct);
            var existing = existingRows.Select(u => new ExistingDirectoryUser(u.Id, u.ExternalId, u.UserName, u.Email, u.GivenName, u.FamilyName, u.Department, u.Active, u.Title)).ToList();
            var plan = SyncPlanner.Build(mapped, existing, forceDisable);

            var errors = new List<string>();
            int created = 0, updated = 0, disabled = 0, enabled = 0;
            if (!dryRun)
            {
                foreach (var a in plan.Actions)
                {
                    try
                    {
                        // Her islemde taze (izlenen) satir: onceki hatada ChangeTracker temizlenmis olabilir.
                        var row = a.DirectoryUserId is { } id ? await _db.DirectoryUsers.FirstAsync(r => r.Id == id, ct) : null;
                        switch (a.Kind)
                        {
                            case SyncActionKind.Create:
                                await _prov.CreateAsync(tenant, settings, DirectorySources.Ldap, Draft(a.Source!, null), ct);
                                created++;
                                break;
                            case SyncActionKind.Update:
                                await _prov.UpdateAsync(tenant, row!, Draft(a.Source!, row), ct);
                                updated++;
                                break;
                            case SyncActionKind.Enable:
                                await _prov.UpdateAsync(tenant, row!, Draft(a.Source!, row), ct);
                                enabled++;
                                break;
                            case SyncActionKind.Disable:
                                await _prov.DeactivateAsync(tenant, row!, ct);
                                disabled++;
                                break;
                        }
                    }
                    catch (Exception ex) when (ex is ScimException or InvalidOperationException)
                    {
                        _db.ChangeTracker.Clear();
                        if (errors.Count < 50) errors.Add($"{a.UserName}: {ex.Message}");
                    }
                }
            }

            // Calisan kaydi daha once olusturulamamis (cakisma, gecici hata) kullanicilar yeniden denenir.
            (int Linked, int Failed) emp = (0, 0);
            if (!dryRun)
            {
                _db.ChangeTracker.Clear();
                emp = await _prov.RetryFailedAsync(tenant.Slug, ct);
            }

            var result = new SyncResult(dryRun, plan.Warning, connWarning, entries.Count,
                dryRun ? plan.Count(SyncActionKind.Create) : created,
                dryRun ? plan.Count(SyncActionKind.Update) : updated,
                dryRun ? plan.Count(SyncActionKind.Disable) : disabled,
                dryRun ? plan.Count(SyncActionKind.Enable) : enabled,
                plan.Count(SyncActionKind.Skip), plan.Count(SyncActionKind.Unchanged),
                plan.Actions.Where(x => x.Kind != SyncActionKind.Unchanged).Take(500)
                    .Select(x => (object)new { kind = x.Kind.ToString(), x.UserName, x.Email, x.Detail }).ToList(),
                errors, emp.Linked, emp.Failed);
            if (!dryRun) await RecordAsync(tenant.Slug, trigger, errors.Count == 0 ? "Success" : "PartialFailure", result, ct);
            return result;
        }
        catch (SyncFailedException ex)
        {
            if (!dryRun) await RecordAsync(tenant.Slug, trigger, "Failed", new { error = ex.Message }, ct);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static ScimUserDraft Draft(LdapMappedUser m, DirectoryUser? row)
    {
        var d = new ScimUserDraft
        {
            UserName = m.UserName,
            ExternalId = m.ExternalId,
            GivenName = m.GivenName,
            FamilyName = m.FamilyName,
            Email = m.Email,
            Department = m.Department,
            Title = m.Title,
            Active = !m.Disabled,
        };
        ScimUserMapper.Validate(d);
        return d;
    }

    private async Task RecordAsync(string slug, string trigger, string status, object summary, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var s = await _db.DirectorySettings.FirstOrDefaultAsync(x => x.TenantSlug == slug, ct);
        if (s is null) return;
        s.LastSyncAt = DateTimeOffset.UtcNow;
        s.LastSyncTrigger = trigger;
        s.LastSyncStatus = status;
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        s.LastSyncSummary = json.Length > 8000 ? json[..8000] : json;
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Arka plan LDAP esitlemesi: LdapEnabled + LdapAutoSync acik ve aktif kiracilar icin
/// DIRECTORY_SYNC_MINUTES (varsayilan 60) dakikada bir. Calisan kayitlari employee-service ic
/// ucuyla (INTERNAL_SERVICE_TOKEN) olusturulur.
/// </summary>
public sealed class DirectorySyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DirectorySyncHostedService> _log;

    public DirectorySyncHostedService(IServiceScopeFactory scopes, ILogger<DirectorySyncHostedService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public static TimeSpan Interval =>
        TimeSpan.FromMinutes(double.TryParse(Environment.GetEnvironmentVariable("DIRECTORY_SYNC_MINUTES"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0.05 ? m : 60);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("tenant/DirectorySyncHostedService", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<string> slugs;
                using (var scope = _scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                    slugs = await db.DirectorySettings.Where(s => s.LdapEnabled && s.LdapAutoSync).Select(s => s.TenantSlug).ToListAsync(ct);
                }
                foreach (var slug in slugs)
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                    var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug && t.Status == TenantStatus.Active, ct);
                    if (tenant is null) continue;
                    var sync = scope.ServiceProvider.GetRequiredService<DirectorySyncService>();
                    try
                    {
                        var r = await sync.RunAsync(tenant, "background", dryRun: false, forceDisable: false, ct);
                        _log.LogInformation("Arka plan dizin esitlemesi {Tenant}: +{C} ~{U} -{D}", slug, r.Created, r.Updated, r.Disabled);
                    }
                    catch (DirectorySyncService.SyncFailedException ex)
                    {
                        _log.LogWarning("Arka plan dizin esitlemesi {Tenant} basarisiz: {Message}", slug, ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Arka plan dizin esitleme turu basarisiz");
            }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
