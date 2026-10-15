using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Calendar;
using GovernanceService.Infrastructure.Provisioning;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma ve
 * askıya alma (İK onaylı). Ayrıntı: Infrastructure/Provisioning.
 *   GET    /account-provisioning                    özellik durumu + sağlayıcı ayarları + bekleyen sayısı
 *   PUT    /account-provisioning/configs/{p}        ayar (sırlar şifreli; bir daha gösterilmez)
 *   DELETE /account-provisioning/configs/{p}
 *   POST   /account-provisioning/configs/{p}/test   bağlantı testi (jeton + salt okunur liste çağrısı)
 *   POST   /account-provisioning/scan               işe giriş/ayrılış taraması (yalnızca Pending istek açar)
 *   GET    /account-provisioning/requests?status=
 *   POST   /account-provisioning/requests           elle istek
 *   POST   /account-provisioning/requests/{id}/approve   onay → sağlayıcı çağrısı (Failed ise yeniden dener)
 *   POST   /account-provisioning/requests/{id}/reject
 * ==================================================================== */
[Route("api/account-provisioning")]
[Authorize(Policy = "RequireHrAdmin")]
public class AccountProvisioningController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly GoogleDirectoryProvisioner _google;
    private readonly MicrosoftDirectoryProvisioner _microsoft;
    private readonly ILogger<AccountProvisioningController> _log;

    public AccountProvisioningController(GovernanceDbContext db, GoogleDirectoryProvisioner google, MicrosoftDirectoryProvisioner microsoft,
        ILogger<AccountProvisioningController> log)
    { _db = db; _google = google; _microsoft = microsoft; _log = log; }

    private IDirectoryProvisioner Of(string provider) => provider == "Google" ? _google : _microsoft;

    private Task AuditAsync(string id, string action, object changes, CancellationToken ct) =>
        ComplianceAudit.WriteAsync(Db, Tenant, "AccountProvisioning", id, action, changes, Me.UserId, Me.Name, ct);

    private static IActionResult Disabled() => new ObjectResult(new
    {
        message = "Hesap açma/kapatma bu kurulumda kapalı (ACCOUNT_PROVISIONING_ENABLED).", code = "feature_disabled",
    }) { StatusCode = 409 };

    private static IActionResult MigrationMissing() =>
        new ObjectResult(new { message = "Bu özellik için veritabanı güncellemesi (2026-10-25_account_provisioning.sql) uygulanmalı.", code = "migration_missing" }) { StatusCode = 503 };

    private static object ShapeConfig(string provider, ProvisioningConfig? c) => new
    {
        provider, configured = c is not null, isEnabled = c?.IsEnabled ?? false, domain = c?.Domain, autoCreate = c?.AutoCreate ?? true,
        autoSuspend = c?.AutoSuspend ?? true, clientId = c?.ClientId, hasCredentials = c?.CredentialsEnc != null, adminSubject = c?.AdminSubject,
        orgUnit = c?.OrgUnit, msTenant = c?.MsTenant, usageLocation = c?.UsageLocation, lastTestAt = c?.LastTestAt, lastError = c?.LastError,
        scopes = provider == "Google" ? GoogleDirectoryProvisioner.Scope : "User.ReadWrite.All (Application)",
    };

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Ok(new { enabled = false, providers = Array.Empty<object>(), pending = 0 });
        try
        {
            var cfgs = await ProvisioningStore.ConfigsAsync(Db, Tenant, ct);
            var pending = Convert.ToInt32(await Db.ScalarAsync(
                "SELECT count(*) FROM governance_provisioning_requests WHERE \"TenantSlug\" = $1 AND \"Status\" IN ('Pending','Failed')", ct, Tenant));
            return Ok(new
            {
                enabled = true, pending,
                providers = AccountNaming.Providers.Select(p => ShapeConfig(p, cfgs.FirstOrDefault(c => c.Provider == p))),
            });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
    }

    public record ConfigInput(string Domain, bool IsEnabled, bool AutoCreate, bool AutoSuspend, string? ClientId, string? Credentials,
        string? AdminSubject, string? OrgUnit, string? MsTenant, string? UsageLocation);

    [HttpPut("configs/{provider}")]
    public async Task<IActionResult> SaveConfig(string provider, ConfigInput body, CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        var p = AccountNaming.NormalizeProvider(provider);
        if (p is null) return NotFound();
        var domain = (body.Domain ?? "").Trim().ToLowerInvariant();
        if (!AccountNaming.ValidDomain(domain)) return BadRequest(new { message = "Geçerli bir alan adı girin (örn. sirket.com)." });
        var existing = await ProvisioningStore.ConfigAsync(Db, Tenant, p, ct);
        var creds = string.IsNullOrWhiteSpace(body.Credentials) ? null : body.Credentials.Trim();
        if (existing is null && creds is null)
            return BadRequest(new { message = p == "Google" ? "Servis hesabı anahtarı (JSON) zorunlu." : "İstemci gizli anahtarı (client secret) zorunlu." });
        string? clientId;
        string? adminSubject = null, orgUnit = null, msTenant = null, usage = null;
        if (p == "Google")
        {
            clientId = existing?.ClientId;
            if (creds is not null)
            {
                var (email, _, error) = AccountNaming.ParseServiceAccount(creds);
                if (error is not null) return BadRequest(new { message = error });
                clientId = email;
            }
            adminSubject = body.AdminSubject?.Trim().ToLowerInvariant();
            if (!AccountNaming.ValidAccountEmail(adminSubject, domain))
                return BadRequest(new { message = "Yönetici hesabı, alan adındaki bir süper yönetici e-postası olmalı." });
            orgUnit = string.IsNullOrWhiteSpace(body.OrgUnit) ? null : body.OrgUnit.Trim();
            if (orgUnit is not null && (!orgUnit.StartsWith('/') || orgUnit.Length > 256))
                return BadRequest(new { message = "Kuruluş birimi yolu \"/\" ile başlamalı (örn. /Çalışanlar)." });
        }
        else
        {
            clientId = body.ClientId?.Trim();
            if (!Guid.TryParse(clientId, out _)) return BadRequest(new { message = "Microsoft uygulama (istemci) kimliği bir GUID olmalı." });
            msTenant = body.MsTenant?.Trim();
            if (!Guid.TryParse(msTenant, out _)) return BadRequest(new { message = "Dizin (kiracı) kimliği bir GUID olmalı." });
            usage = string.IsNullOrWhiteSpace(body.UsageLocation) ? null : body.UsageLocation.Trim().ToUpperInvariant();
            if (usage is not null && (usage.Length != 2 || !usage.All(char.IsAsciiLetterUpper)))
                return BadRequest(new { message = "Kullanım konumu iki harfli ülke kodu olmalı (örn. TR)." });
        }
        if (body.IsEnabled && await TransferGuard.MissingAsync(_db, Tenant, TransferGuard.KeyOf(p), ct) is { } transferError)
            return BadRequest(new { message = transferError, code = "kvkk_transfer" });

        var enc = creds is null ? null : SecretBox.Protect(creds);
        await Db.ExecuteAsync("""
            INSERT INTO governance_provisioning_configs ("Id","TenantSlug","Provider","IsEnabled","Domain","AutoCreate","AutoSuspend","ClientId",
                "CredentialsEnc","AdminSubject","OrgUnit","MsTenant","UsageLocation","CreatedAt","UpdatedAt","UpdatedBy")
            VALUES (gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,now(),now(),$13)
            ON CONFLICT ("TenantSlug","Provider") DO UPDATE SET
                "IsEnabled" = EXCLUDED."IsEnabled", "Domain" = EXCLUDED."Domain", "AutoCreate" = EXCLUDED."AutoCreate",
                "AutoSuspend" = EXCLUDED."AutoSuspend", "ClientId" = EXCLUDED."ClientId",
                "CredentialsEnc" = coalesce(EXCLUDED."CredentialsEnc", governance_provisioning_configs."CredentialsEnc"),
                "AdminSubject" = EXCLUDED."AdminSubject", "OrgUnit" = EXCLUDED."OrgUnit", "MsTenant" = EXCLUDED."MsTenant",
                "UsageLocation" = EXCLUDED."UsageLocation", "UpdatedAt" = now(), "UpdatedBy" = EXCLUDED."UpdatedBy",
                "LastError" = CASE WHEN EXCLUDED."CredentialsEnc" IS NULL THEN governance_provisioning_configs."LastError" END
            """, ct, Tenant, p, body.IsEnabled, domain, body.AutoCreate, body.AutoSuspend, clientId, enc, adminSubject, orgUnit, msTenant, usage, Me.UserId);
        // Sırlar denetim kaydına yazılmaz; yalnızca değiştiği bilgisi.
        await AuditAsync(p, existing is null ? "ConfigCreated" : "ConfigUpdated",
            new { provider = p, domain, body.IsEnabled, body.AutoCreate, body.AutoSuspend, credentialsChanged = creds is not null }, ct);
        return Ok(new { provider = p, isEnabled = body.IsEnabled });
    }

    [HttpDelete("configs/{provider}")]
    public async Task<IActionResult> DeleteConfig(string provider, CancellationToken ct)
    {
        var p = AccountNaming.NormalizeProvider(provider);
        if (p is null) return NotFound();
        var n = await Db.ExecuteAsync("DELETE FROM governance_provisioning_configs WHERE \"TenantSlug\" = $1 AND \"Provider\" = $2", ct, Tenant, p);
        if (n == 0) return NotFound();
        await AuditAsync(p, "ConfigDeleted", new { provider = p }, ct);
        return NoContent();
    }

    [HttpPost("configs/{provider}/test")]
    public async Task<IActionResult> TestConfig(string provider, CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        var p = AccountNaming.NormalizeProvider(provider);
        if (p is null) return NotFound();
        var cfg = await ProvisioningStore.ConfigAsync(Db, Tenant, p, ct);
        if (cfg is null) return NotFound(new { message = "Sağlayıcı yapılandırılmamış." });
        string? error = null;
        try { await Of(p).TestAsync(cfg, ct); }
        catch (ProviderApiException ex) { error = ex.Message; }
        catch (HttpRequestException ex) { error = "Sağlayıcıya ulaşılamadı: " + ex.Message; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = "Sağlayıcı zamanında yanıt vermedi."; }
        error = Clip(error);
        await Db.ExecuteAsync("UPDATE governance_provisioning_configs SET \"LastTestAt\" = now(), \"LastError\" = $3 WHERE \"TenantSlug\" = $1 AND \"Provider\" = $2",
            ct, Tenant, p, error);
        return Ok(new { ok = error is null, message = error ?? "Bağlantı başarılı." });
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan(CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        int create = 0, suspend = 0;
        foreach (var cfg in (await ProvisioningStore.ConfigsAsync(Db, Tenant, ct)).Where(c => c.IsEnabled))
        {
            var (c, s) = await ProvisioningStore.ScanAsync(Db, cfg, ct);
            create += c; suspend += s;
        }
        return Ok(new { create, suspend });
    }

    [HttpGet("requests")]
    public async Task<IActionResult> Requests([FromQuery] string? status, CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        var filter = status is "Pending" or "Done" or "Failed" or "Rejected" or "Processing" ? status : null;
        try
        {
            var rows = await Db.QueryAsync("""
                SELECT q."Id", q."EmployeeId", e."FirstName" || ' ' || e."LastName", e."Status", e."HireDate", q."Provider", q."Action", q."Status",
                       q."Source", q."AccountEmail", q."Note", q."Error", q."RequestedByName", q."DecidedByName", q."DecidedAt", q."Attempts",
                       q."CreatedAt", q."CompletedAt"
                FROM governance_provisioning_requests q
                LEFT JOIN employee_employees e ON e."Id" = q."EmployeeId" AND e."TenantSlug" = q."TenantSlug"
                WHERE q."TenantSlug" = $1 AND ($2::text IS NULL OR q."Status" = $2)
                ORDER BY CASE q."Status" WHEN 'Pending' THEN 0 WHEN 'Failed' THEN 1 WHEN 'Processing' THEN 2 ELSE 3 END, q."CreatedAt" DESC
                LIMIT 300
                """, r => new
                {
                    id = r.GetGuid(0), employeeId = r.GetGuid(1), employeeName = r.Str(2), employeeStatus = r.Str(3), hireDate = r.Date(4),
                    provider = r.GetString(5), action = r.GetString(6), status = r.GetString(7), source = r.GetString(8), accountEmail = r.GetString(9),
                    note = r.Str(10), error = r.Str(11), requestedByName = r.Str(12), decidedByName = r.Str(13), decidedAt = r.Ts(14),
                    attempts = r.GetInt32(15), createdAt = r.GetFieldValue<DateTime>(16), completedAt = r.Ts(17),
                }, ct, Tenant, filter);
            return Ok(rows);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { return MigrationMissing(); }
    }

    public record RequestInput(Guid EmployeeId, string Provider, string Action, string? AccountEmail, string? Note);

    [HttpPost("requests")]
    public async Task<IActionResult> Create(RequestInput body, CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        var p = AccountNaming.NormalizeProvider(body.Provider);
        if (p is null) return BadRequest(new { message = "Sağlayıcı Google ya da Microsoft olmalı." });
        if (body.Action is not ("Create" or "Suspend")) return BadRequest(new { message = "İşlem Create ya da Suspend olmalı." });
        var cfg = await ProvisioningStore.ConfigAsync(Db, Tenant, p, ct);
        if (cfg is null) return BadRequest(new { message = "Sağlayıcı yapılandırılmamış." });
        var emp = (await Db.QueryAsync("""SELECT "FirstName", "LastName", "Email" FROM employee_employees WHERE "TenantSlug" = $1 AND "Id" = $2""",
            r => (First: r.GetString(0), Last: r.GetString(1), Email: r.Str(2)), ct, Tenant, body.EmployeeId)).FirstOrDefault();
        if (emp.First is null) return NotFound(new { message = "Çalışan bulunamadı." });

        var email = body.AccountEmail?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
        {
            if (body.Action == "Create") email = AccountNaming.Propose(emp.First, emp.Last, cfg.Domain);
            else
            {
                email = (await Db.ScalarAsync("""
                    SELECT "AccountEmail" FROM governance_provisioning_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Provider" = $3
                      AND "Action" = 'Create' AND "Status" = 'Done' ORDER BY "CompletedAt" DESC NULLS LAST LIMIT 1
                    """, ct, Tenant, body.EmployeeId, p)) as string ?? emp.Email?.ToLowerInvariant();
            }
        }
        if (!AccountNaming.ValidAccountEmail(email, cfg.Domain))
            return BadRequest(new { message = $"Hesap adresi {cfg.Domain} alan adında geçerli bir adres olmalı." });
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()[..Math.Min(body.Note.Trim().Length, 500)];
        var id = await ProvisioningStore.InsertRequestAsync(Db, Tenant, body.EmployeeId, p, body.Action, "Manual", email!, note, Me.UserId, Me.Name, ct);
        if (id is null) return Conflict(new { message = "Bu çalışan için aynı işlemde açık ya da tamamlanmış bir istek var." });
        await AuditAsync(id.Value.ToString(), "Requested", new { provider = p, action = body.Action, employeeId = body.EmployeeId, accountEmail = email }, ct);
        return Ok(new { id, accountEmail = email });
    }

    public record ApproveInput(string? AccountEmail);

    [HttpPost("requests/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, ApproveInput? body, CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        var req = (await Db.QueryAsync("""
            SELECT "EmployeeId", "Provider", "Action", "Status", "Source", "AccountEmail", "RequestedBy"
            FROM governance_provisioning_requests WHERE "TenantSlug" = $1 AND "Id" = $2
            """, r => new { EmployeeId = r.GetGuid(0), Provider = r.GetString(1), Action = r.GetString(2), Status = r.GetString(3),
                Source = r.GetString(4), AccountEmail = r.GetString(5), RequestedBy = r.Str(6) }, ct, Tenant, id)).FirstOrDefault();
        if (req is null) return NotFound();
        if (req.Status is not ("Pending" or "Failed")) return Conflict(new { message = "İstek zaten sonuçlanmış." });
        var me = await MyPersonAsync(ct);
        if (AccountNaming.DecisionBlock(req.Source, req.RequestedBy, Me.UserId, req.EmployeeId, me?.Id) is { } block)
            return StatusCode(403, new { message = block, code = "sod" });
        var cfg = await ProvisioningStore.ConfigAsync(Db, Tenant, req.Provider, ct);
        if (cfg is null || !cfg.IsEnabled) return BadRequest(new { message = "Sağlayıcı yapılandırılmamış ya da kapalı." });
        if (await TransferGuard.MissingAsync(_db, Tenant, TransferGuard.KeyOf(req.Provider), ct) is { } transferError)
            return BadRequest(new { message = transferError, code = "kvkk_transfer" });

        var email = req.AccountEmail;
        if (req.Action == "Create" && !string.IsNullOrWhiteSpace(body?.AccountEmail)) email = body.AccountEmail.Trim().ToLowerInvariant();
        if (!AccountNaming.ValidAccountEmail(email, cfg.Domain))
            return BadRequest(new { message = $"Hesap adresi {cfg.Domain} alan adında geçerli bir adres olmalı." });

        var emp = (await Db.QueryAsync("""SELECT "FirstName", "LastName" FROM employee_employees WHERE "TenantSlug" = $1 AND "Id" = $2""",
            r => (First: r.GetString(0), Last: r.GetString(1)), ct, Tenant, req.EmployeeId)).FirstOrDefault();
        if (emp.First is null) return NotFound(new { message = "Çalışan bulunamadı." });

        // Aynı isteği iki kişi aynı anda onaylarsa yalnızca biri çağrı yapar.
        int claimed;
        try
        {
            claimed = await Db.ExecuteAsync("""
                UPDATE governance_provisioning_requests SET "Status" = 'Processing', "AccountEmail" = $3, "DecidedBy" = $4, "DecidedByName" = $5,
                       "DecidedAt" = now(), "Attempts" = "Attempts" + 1
                WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" IN ('Pending','Failed')
                """, ct, Tenant, id, email, Me.UserId, Me.Name);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505")
        {
            return Conflict(new { message = "Bu çalışan için aynı işlemde açık ya da tamamlanmış başka bir istek var." });
        }
        if (claimed == 0) return Conflict(new { message = "İstek zaten sonuçlanmış." });
        await AuditAsync(id.ToString(), "Approved", new { provider = req.Provider, action = req.Action, employeeId = req.EmployeeId, accountEmail = email }, ct);

        string? error = null, externalId = null, password = null;
        try
        {
            if (req.Action == "Create")
            {
                password = AccountNaming.TemporaryPassword();
                externalId = await Of(req.Provider).CreateUserAsync(cfg, new NewAccount(email!, emp.First, emp.Last, password), CancellationToken.None);
            }
            else await Of(req.Provider).SuspendUserAsync(cfg, email!, CancellationToken.None);
        }
        catch (ProviderApiException ex) { error = ex.Message; }
        catch (HttpRequestException ex) { error = "Sağlayıcıya ulaşılamadı: " + ex.Message; }
        catch (TaskCanceledException) { error = "Sağlayıcı zamanında yanıt vermedi."; }
        // İstek "Processing"te takılı kalmasın (ör. şifreleme anahtarı yok, bozuk yanıt): Failed olur, yeniden denenebilir.
        catch (Exception ex) { error = ex is InvalidOperationException ? ex.Message : "Beklenmeyen hata: " + ex.GetType().Name; }
        error = Clip(error);

        await Db.ExecuteAsync("""
            UPDATE governance_provisioning_requests SET "Status" = $3, "Error" = $4, "ExternalId" = coalesce($5, "ExternalId"),
                   "CompletedAt" = CASE WHEN $3 = 'Done' THEN now() END
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, CancellationToken.None, Tenant, id, error is null ? "Done" : "Failed", error, externalId);
        await AuditAsync(id.ToString(), error is null ? (req.Action == "Create" ? "AccountCreated" : "AccountSuspended") : "ProviderFailed",
            new { provider = req.Provider, action = req.Action, employeeId = req.EmployeeId, accountEmail = email, error }, CancellationToken.None);
        if (error is not null)
        {
            _log.LogWarning("Hesap sağlama başarısız: kiracı {Tenant}, istek {Id}, {Provider} {Action}: {Error}", Tenant, id, req.Provider, req.Action, error);
            return StatusCode(502, new { ok = false, message = error });
        }
        // Geçici parola yalnızca bu yanıtta bir kez döner; saklanmaz, günlüğe ve denetim kaydına yazılmaz.
        return Ok(new { ok = true, status = "Done", accountEmail = email, initialPassword = password });
    }

    public record RejectInput(string? Reason);

    [HttpPost("requests/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, RejectInput? body, CancellationToken ct)
    {
        if (!ProvisioningFlag.Enabled) return Disabled();
        var reason = string.IsNullOrWhiteSpace(body?.Reason) ? null : body!.Reason!.Trim()[..Math.Min(body.Reason.Trim().Length, 500)];
        var n = await Db.ExecuteAsync("""
            UPDATE governance_provisioning_requests SET "Status" = 'Rejected', "DecidedBy" = $3, "DecidedByName" = $4, "DecidedAt" = now(),
                   "Note" = coalesce($5, "Note")
            WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" IN ('Pending','Failed')
            """, ct, Tenant, id, Me.UserId, Me.Name, reason);
        if (n == 0) return Conflict(new { message = "İstek bulunamadı ya da zaten sonuçlanmış." });
        await AuditAsync(id.ToString(), "Rejected", new { reason }, ct);
        return Ok(new { status = "Rejected" });
    }

    private static string? Clip(string? s) => s is null ? null : s.Length > 500 ? s[..500] : s;
}
