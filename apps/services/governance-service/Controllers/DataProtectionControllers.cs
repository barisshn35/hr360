using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Güvenlik dalgası 2B — veri koruma (Yönetim › Veri koruma).
 *
 *  - Ayarlar: bordro görevler ayrılığı (varsayılan açık; kapatmak yalnızca şirket
 *    yöneticisi + gerekçe, denetim kaydına yazılır), toplu görüntüleme eşiği ve
 *    isteğe bağlı geçici engel, uyarı alıcıları.
 *  - Uyarılar: toplu görüntüleme dedektörünün bulguları (MassViewWorker).
 *  - İz kodu: filigranlı çıktı için kod üretme (her oturum) ve kodla kaynağı bulma (İK).
 *  - Erişim gözden geçirme kampanyası: İK başlatır, her yönetici ekibinin rol/izinlerini
 *    "uygun" / "kaldırılsın" diye işaretler, İK kaldırmaları mevcut rol uçlarıyla uygular.
 *    Roller Keycloak'ta olduğundan tenant-service'in mevcut uçları çağıranın jetonuyla çağrılır
 *    (KeycloakAdminClient'a dokunulmaz).
 * ==================================================================== */

[Route("api/data-protection")]
[Authorize]
public class DataProtectionController : AppController
{
    private bool IsTenantAdmin => Me.Roles.Contains("tenant-admin");

    // ------------------------------------------------------------------ ayarlar

    [HttpGet("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await SecuritySettingsStore.LoadAsync(Db, Tenant, ct);
        var people = await People.ListAsync(Tenant, ct);
        return Ok(new
        {
            s.PayrollSod, s.PayrollSodReason, s.MassViewThreshold, s.MassViewWindowMinutes, s.MassViewBlock,
            alertRecipients = s.AlertEmployeeIds.Select(id => new { id, name = people.FirstOrDefault(p => p.Id == id)?.Name }),
            s.UpdatedAt, s.UpdatedBy, blockMinutes = SecuritySettings.BlockMinutes, canEdit = IsTenantAdmin,
        });
    }

    public record SettingsInput(bool PayrollSod, string? PayrollSodReason, int MassViewThreshold, int MassViewWindowMinutes,
        bool MassViewBlock, List<Guid>? AlertEmployeeIds);

    /// <summary>Yalnızca şirket yöneticisi. Görevler ayrılığını kapatmak gerekçe ister; her değişiklik denetim kaydına yazılır.</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] SettingsInput b, CancellationToken ct)
    {
        if (!IsTenantAdmin) return StatusCode(403, new { message = "Veri koruma ayarlarını yalnızca şirket yöneticisi değiştirebilir." });
        if (SecuritySettings.Validate(b.MassViewThreshold, b.MassViewWindowMinutes) is { } err) return BadRequest(new { message = err });
        var old = await SecuritySettingsStore.LoadAsync(Db, Tenant, ct);
        var reason = b.PayrollSodReason?.Trim();
        if (!b.PayrollSod && old.PayrollSod && (reason is null || reason.Length < 10))
            return BadRequest(new { message = "Görevler ayrılığını kapatmak için en az 10 karakterlik gerekçe yazın.", code = "reason_required" });
        var ids = (b.AlertEmployeeIds ?? new()).Distinct().Take(20).ToArray();
        if (ids.Length > 0)
        {
            var known = (await People.ListAsync(Tenant, ct)).Select(p => p.Id).ToHashSet();
            if (ids.Any(i => !known.Contains(i))) return BadRequest(new { message = "Uyarı alıcısı bulunamadı." });
        }
        var next = new SecuritySettings(b.PayrollSod, b.PayrollSod ? null : (reason ?? old.PayrollSodReason), b.MassViewThreshold,
            b.MassViewWindowMinutes, b.MassViewBlock, ids, null, Me.Name);
        await SecuritySettingsStore.SaveAsync(Db, Tenant, next, Me.Name, ct);
        await ComplianceAudit.WriteAsync(Db, Tenant, "SecuritySettings", Tenant, "Updated", new
        {
            payrollSod = new { old = old.PayrollSod, @new = next.PayrollSod }, payrollSodReason = next.PayrollSodReason,
            massViewThreshold = new { old = old.MassViewThreshold, @new = next.MassViewThreshold },
            massViewWindowMinutes = new { old = old.MassViewWindowMinutes, @new = next.MassViewWindowMinutes },
            massViewBlock = new { old = old.MassViewBlock, @new = next.MassViewBlock }, alertRecipients = ids.Length,
        }, Me.UserId, Me.Name, ct);
        return await GetSettings(ct);
    }

    // ------------------------------------------------------------------ uyarılar

    [HttpGet("alerts")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Alerts([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        try
        {
            return Ok(await Db.QueryAsync("""
                SELECT "Id","Kind","UserName","DistinctCount","WindowMinutes","Threshold","DetectedAt","BlockedUntil","AcknowledgedAt","AcknowledgedBy"
                FROM governance_security_alerts WHERE "TenantSlug" = $1 AND "DetectedAt" > now() - make_interval(days => $2)
                ORDER BY "DetectedAt" DESC LIMIT 200
                """, r => new
            {
                id = r.GetGuid(0), kind = r.GetString(1), userName = r.Str(2), distinctCount = r.GetInt32(3), windowMinutes = r.GetInt32(4),
                threshold = r.GetInt32(5), detectedAt = r.GetFieldValue<DateTime>(6), blockedUntil = r.Ts(7),
                blocked = r.Ts(7) is { } u && u > DateTime.UtcNow, acknowledgedAt = r.Ts(8), acknowledgedBy = r.Str(9),
            }, ct, Tenant, days));
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return Ok(Array.Empty<object>());
        }
    }

    public record AckInput(bool Unblock);

    /// <summary>Uyarıyı incelendi olarak işaretler; istenirse geçici engeli kaldırır (denetim kaydına yazılır).</summary>
    [HttpPost("alerts/{id:guid}/ack")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Ack(Guid id, [FromBody] AckInput? b, CancellationToken ct)
    {
        var n = await Db.ExecuteAsync("""
            UPDATE governance_security_alerts SET "AcknowledgedAt" = coalesce("AcknowledgedAt", now()), "AcknowledgedBy" = coalesce("AcknowledgedBy", $3),
                   "BlockedUntil" = CASE WHEN $4 AND "BlockedUntil" > now() THEN now() ELSE "BlockedUntil" END
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, Me.Name, b?.Unblock == true);
        if (n == 0) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "SecurityAlert", id.ToString(), b?.Unblock == true ? "Unblocked" : "Acknowledged", new { }, Me.UserId, Me.Name, ct);
        return Ok(new { id, acknowledged = true });
    }

    // ------------------------------------------------------------------ filigran / iz kodu

    /// <summary>Kullanıcı başına 10 dakikada en çok 60 iz kodu (toplu indirme/denetim kaydı şişirme sınırı).</summary>
    private static readonly WindowLimiter TraceLimiter = new(60, TimeSpan.FromMinutes(10));

    public record TraceInput(string Kind, string? Subject, string? Format, int? Rows);

    /// <summary>
    /// Yazdırılan/indirilen çıktı için iz kodu ve filigran metni üretir; kod denetim kaydına ("Exported")
    /// bağlanır. Yalnızca arayüzün ürettiği çıktı türleri kabul edilir (<see cref="TracePolicy"/>) ve her tür
    /// kendi görüntüleme iznini ister; bordro pusulası ve sertifikada kişinin kendi kaydı her zaman serbesttir.
    /// </summary>
    [HttpPost("trace")]
    public async Task<IActionResult> Trace([FromBody] TraceInput b, CancellationToken ct)
    {
        var d = TracePolicy.Evaluate(b.Kind, b.Subject, b.Format);
        if (d.Kind is null)
            return BadRequest(new
            {
                message = d.Error switch
                {
                    "subject" => L("Geçersiz çıktı konusu.", "Invalid output subject."),
                    "format" => L("Geçersiz çıktı biçimi.", "Invalid output format."),
                    _ => L("Geçersiz çıktı türü.", "Invalid output type."),
                },
                code = "invalid_trace_" + d.Error,
            });
        if (!TraceLimiter.Allow($"{Tenant}:{Me.UserId}"))
            return StatusCode(429, new { message = L("Çok fazla indirme isteği; birkaç dakika sonra tekrar deneyin.", "Too many download requests; try again in a few minutes.") });
        var allowed = TracePolicy.RolesAllow(d, Me.Roles) || await OwnsAsync(d, ct);
        if (!allowed)
            return StatusCode(403, new { message = L("Bu çıktıyı indirme yetkiniz yok.", "You are not allowed to download this output."), code = "trace_forbidden" });
        var code = Watermark.NewCode();
        var at = DateTimeOffset.UtcNow;
        await Watermark.AuditAsync(Db, Tenant, b.Kind.Trim(), d.Subject, d.Format, b.Rows is { } r ? Math.Clamp(r, 0, 10_000_000) : null, code, Me.UserId, Me.Name, ct);
        return Ok(new { code, name = Me.Name, at, text = Watermark.Text(Me.Name, at, code) });
    }

    /// <summary>Kişinin kendi bordro pusulası / sertifikası mı (salt okuma; kiracıya göre süzülü).</summary>
    private async Task<bool> OwnsAsync(TracePolicy.Decision d, CancellationToken ct)
    {
        if (d.Kind is not (TracePolicy.Kind.Payslip or TracePolicy.Kind.Certificate)) return false;
        // Sertifikada kod gönderilmemişse (doğrulama kodu/kimlik no yok) kişinin kendi çıktısı sayılır.
        if (d.Kind == TracePolicy.Kind.Certificate && d.Subject is null) return true;
        var me = await People.FindByUserAsync(Tenant, Me.UserId, ct);
        if (me is null) return false;
        try
        {
            return d.Kind == TracePolicy.Kind.Payslip
                ? await Db.ScalarAsync("""SELECT 1 FROM compensation_payslips WHERE "TenantSlug" = $1 AND "Id" = $2::uuid AND "EmployeeId" = $3""", ct, Tenant, d.Subject, me.Id) is not null
                : await Db.ScalarAsync("""
                    SELECT 1 FROM learning_certifications WHERE "TenantSlug" = $1 AND "EmployeeId" = $3
                      AND ("VerificationCode" = $2 OR "CredentialId" = $2) LIMIT 1
                    """, ct, Tenant, d.Subject, me.Id) is not null;
        }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42703") { return false; }
    }

    /// <summary>İz kodundan kaynağı bulur: kim, ne zaman, hangi çıktı. Yalnızca İK/şirket yöneticisi; sorgu denetim kaydına yazılır.</summary>
    [HttpGet("trace/{code}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> FindTrace(string code, CancellationToken ct)
    {
        var norm = Watermark.NormalizeCode(code);
        if (norm is null) return BadRequest(new { message = "İz kodu 8 karakter olmalı (ör. K7P2-MX9Q)." });
        var row = (await Db.QueryAsync("""
            SELECT "Service","EntityType","EntityId","Action","UserId","UserName","OccurredAt","Changes"::text, "Id"
            FROM audit_log WHERE "TenantSlug" = $1 AND "Changes" ? 'traceCode' AND "Changes"->>'traceCode' = $2 ORDER BY "Id" LIMIT 1
            """, r => new
        {
            service = r.GetString(0), entityType = r.GetString(1), entityId = r.Str(2), action = r.GetString(3),
            userName = r.Str(5), occurredAt = r.GetFieldValue<DateTime>(6), changes = JsonDocument.Parse(r.Str(7) ?? "{}").RootElement, auditId = r.GetInt64(8),
        }, ct, Tenant, norm)).FirstOrDefault();
        await ComplianceAudit.WriteAsync(Db, Tenant, "TraceLookup", norm, "Viewed", new { found = row is not null }, Me.UserId, Me.Name, ct);
        if (row is null) return NotFound(new { message = "Bu iz koduyla bir kayıt bulunamadı." });
        return Ok(new { code = norm, row.service, row.entityType, row.entityId, row.action, row.userName, row.occurredAt, row.changes, row.auditId });
    }
}

/// <summary>Erişim gözden geçirme kampanyaları (rol/izin yeniden onayı).</summary>
[Route("api/data-protection/access-reviews")]
[Authorize]
public class AccessReviewController : AppController
{
    private readonly IHttpClientFactory _http;
    public AccessReviewController(IHttpClientFactory http) => _http = http;

    private static readonly string TenantServiceUrl = EnvVar.Or("TENANT_SERVICE_URL", "http://tenant-service:8080").TrimEnd('/');

    /// <summary>Gözden geçirmede gösterilen standart roller (employee temel roldür, kaldırma seçeneği olarak sunulmaz).</summary>
    public static readonly string[] ElevatedRoles = { "manager", "accounting", "hr-admin", "tenant-admin" };

    /// <summary>tenant-service'in mevcut uçlarını ÇAĞIRANIN jetonuyla çağırır (yetki kontrolü orada).</summary>
    private async Task<(int Status, string Body)> TenantApiAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, TenantServiceUrl + path);
        if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var auth)) req.Headers.Authorization = auth;
        if (Request.Headers["X-Correlation-Id"].FirstOrDefault() is { } cid) req.Headers.TryAddWithoutValidation("X-Correlation-Id", cid);
        if (body is not null) req.Content = JsonContent.Create(body);
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var res = await client.SendAsync(req, ct);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>tenant-service üye satırı; çalışan kaydı olmayan hesapta EmployeeId boş, HasEmployeeRecord false.</summary>
    public sealed record MemberRow(Guid? EmployeeId, string? KeycloakUserId, bool HasLoginAccess, List<string>? Roles, List<string>? ExtraPermissions,
        bool HasEmployeeRecord = true, string? FirstName = null, string? LastName = null);

    /// <summary>
    /// Kampanyaya girecek satırlar (saf; birim testli): çalışan kaydı olanlar kiracının çalışan listesinde
    /// bulunmalı; çalışan kaydı olmayan hesaplar (ör. yalnızca yönetim için açılmış şirket yöneticisi) da girer.
    /// </summary>
    public static List<MemberRow> CampaignRows(IEnumerable<MemberRow> members, IReadOnlySet<Guid> knownEmployees) =>
        Reviewable(members).Where(m => m.EmployeeId is { } e ? knownEmployees.Contains(e) : !m.HasEmployeeRecord).ToList();

    /// <summary>Çalışan kaydı olmayan hesabın görünen adı (ad soyad; yoksa boş).</summary>
    public static string? AccountName(MemberRow m) =>
        m.EmployeeId is not null ? null : $"{m.FirstName} {m.LastName}".Trim() is { Length: > 0 } n ? n[..Math.Min(200, n.Length)] : null;

    /// <summary>Gözden geçirilecek kişiler: giriş hesabı olan ve temel rolün ötesinde rolü/ek izni bulunanlar.</summary>
    public static List<MemberRow> Reviewable(IEnumerable<MemberRow> members) => members
        .Where(m => m.HasLoginAccess && !string.IsNullOrEmpty(m.KeycloakUserId))
        .Where(m => (m.Roles ?? new()).Any(r => ElevatedRoles.Contains(r)) || (m.ExtraPermissions ?? new()).Count > 0)
        .ToList();

    /// <summary>Gözden geçiren: çalışanın departman başı (kendisi değilse); yoksa İK (null).</summary>
    public static Guid? ReviewerFor(Person? p) => p?.DepartmentHeadId is { } h && h != p.Id ? h : null;

    private sealed record Item(Guid Id, Guid ReviewId, Guid? EmployeeId, string KeycloakUserId, string[] Roles, string[] Permissions,
        Guid? ReviewerEmployeeId, string? Decision, string[] RemoveRoles, string? Note, string? DecidedByName, DateTime? DecidedAt,
        DateTime? AppliedAt, string? ApplyResult, string? AccountName);

    private const string ItemCols = """
        "Id","ReviewId","EmployeeId","KeycloakUserId","Roles","Permissions","ReviewerEmployeeId","Decision","RemoveRoles","Note","DecidedByName","DecidedAt","AppliedAt","ApplyResult","AccountName"
        """;
    /// <summary>ItemCols sütun sayısı: ek sütunlar (kampanya başlığı vb.) bu sıradan başlar.</summary>
    private const int ItemColCount = 15;
    private static Item MapItem(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GuidOrNull(2), r.GetString(3),
        r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5), r.GuidOrNull(6), r.Str(7), r.GetFieldValue<string[]>(8), r.Str(9), r.Str(10),
        r.Ts(11), r.Ts(12), r.Str(13), r.Str(14));

    private static object Shape(Item i, IReadOnlyDictionary<Guid, Person> people) => new
    {
        i.Id, i.ReviewId, i.EmployeeId, hasEmployeeRecord = i.EmployeeId is not null,
        employeeName = i.EmployeeId is { } e ? people.GetValueOrDefault(e)?.Name : i.AccountName,
        position = i.EmployeeId is { } e2 ? people.GetValueOrDefault(e2)?.Position : null,
        department = i.EmployeeId is { } e3 ? people.GetValueOrDefault(e3)?.Department : null,
        roles = i.Roles, permissions = i.Permissions, i.ReviewerEmployeeId,
        reviewerName = i.ReviewerEmployeeId is { } r ? people.GetValueOrDefault(r)?.Name : null,
        i.Decision, removeRoles = i.RemoveRoles, i.Note, decidedBy = i.DecidedByName, i.DecidedAt, i.AppliedAt, i.ApplyResult,
    };

    private async Task<Dictionary<Guid, Person>> PeopleMapAsync(CancellationToken ct) =>
        (await People.ListAsync(Tenant, ct, includeTerminated: true)).ToDictionary(p => p.Id);

    // ------------------------------------------------------------------ İK: kampanyalar

    [HttpGet]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        try
        {
            var rows = await Db.QueryAsync("""
                SELECT r."Id", r."Title", r."Status", r."DueDate", r."CreatedByName", r."CreatedAt", r."ClosedAt",
                       count(i."Id"), count(i."Decision"), count(*) FILTER (WHERE i."Decision" = 'Remove'), count(i."AppliedAt")
                FROM governance_access_reviews r LEFT JOIN governance_access_review_items i ON i."ReviewId" = r."Id"
                WHERE r."TenantSlug" = $1 GROUP BY r."Id" ORDER BY r."CreatedAt" DESC LIMIT 50
                """, r => new
            {
                id = r.GetGuid(0), title = r.GetString(1), status = r.GetString(2), dueDate = r.Date(3), createdBy = r.Str(4),
                createdAt = r.GetFieldValue<DateTime>(5), closedAt = r.Ts(6), total = r.GetInt64(7), decided = r.GetInt64(8),
                removals = r.GetInt64(9), applied = r.GetInt64(10),
            }, ct, Tenant);
            var last = rows.Count == 0 ? (DateTime?)null : rows.Max(x => x.createdAt);
            return Ok(new { items = rows, nextDue = last?.AddDays(90), overdue = last is null || last.Value.AddDays(90) < DateTime.UtcNow });
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return Ok(new { items = Array.Empty<object>(), nextDue = (DateTime?)null, overdue = true });
        }
    }

    public record CreateInput(string? Title, DateOnly? DueDate);

    /// <summary>
    /// Yeni kampanya: tenant-service'ten (çağıranın jetonuyla) kullanıcı/rol listesi alınır, rolü/ek izni olan
    /// her kişi için gözden geçirme satırı açılır; gözden geçiren kişinin departman başıdır (yoksa İK).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateInput b, CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(b.Title) ? $"Erişim gözden geçirme {DateTime.UtcNow:yyyy}-Ç{(DateTime.UtcNow.Month - 1) / 3 + 1}" : b.Title.Trim();
        if (title.Length > 120) return BadRequest(new { message = "Başlık en fazla 120 karakter olabilir." });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var due = b.DueDate ?? today.AddDays(14);
        if (due < today || due > today.AddDays(90)) return BadRequest(new { message = "Son tarih bugün ile 90 gün sonrası arasında olmalı." });
        var open = await Db.ScalarAsync("""SELECT 1 FROM governance_access_reviews WHERE "TenantSlug" = $1 AND "Status" = 'Open' LIMIT 1""", ct, Tenant);
        if (open is not null) return Conflict(new { message = "Açık bir erişim gözden geçirme kampanyası var; önce onu kapatın." });

        var (status, body) = await TenantApiAsync(HttpMethod.Get, "/api/my-tenant/members", null, ct);
        if (status is 401 or 403) return StatusCode(403, new { message = "Kullanıcı rollerini okuma yetkiniz yok." });
        if (status != 200) return StatusCode(502, new { message = "Kullanıcı rolleri alınamadı (tenant-service)." });
        var members = JsonSerializer.Deserialize<List<MemberRow>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        var people = await PeopleMapAsync(ct);
        var rows = CampaignRows(members, people.Keys.ToHashSet());
        if (rows.Count == 0) return BadRequest(new { message = "Gözden geçirilecek (rolü ya da ek izni olan) kullanıcı yok." });

        var id = Guid.NewGuid();
        await using var conn = await Db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO governance_access_reviews ("Id","TenantSlug","Title","Status","DueDate","CreatedBy","CreatedByName","CreatedAt")
            VALUES ($1,$2,$3,'Open',$4,$5,$6,now())
            """, conn, tx))
        {
            foreach (var v in new object[] { id, Tenant, title, due, Me.UserId, Me.Name }) cmd.Parameters.Add(new NpgsqlParameter { Value = v });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var m in rows)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO governance_access_review_items ("Id","TenantSlug","ReviewId","EmployeeId","KeycloakUserId","Roles","Permissions","ReviewerEmployeeId","RemoveRoles","AccountName")
                VALUES (gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,'{}',$8)
                """, conn, tx);
            // Çalışan kaydı olmayan hesabın gözden geçireni İK'dır (ReviewerEmployeeId boş).
            cmd.Parameters.Add(new NpgsqlParameter { Value = Tenant });
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = m.EmployeeId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = m.KeycloakUserId! });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (m.Roles ?? new()).Where(r => ElevatedRoles.Contains(r)).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (m.ExtraPermissions ?? new()).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = m.EmployeeId is { } emp ? ReviewerFor(people.GetValueOrDefault(emp)) : null, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = AccountName(m), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        await ComplianceAudit.WriteAsync(Db, Tenant, "AccessReview", id.ToString(), "Created", new { title, items = rows.Count, dueDate = due }, Me.UserId, Me.Name, ct);
        await NotifyReviewersAsync(id, ct);
        return Ok(new { id, title, items = rows.Count, dueDate = due });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var head = (await Db.QueryAsync("""
            SELECT "Title","Status","DueDate","CreatedByName","CreatedAt","ClosedAt" FROM governance_access_reviews WHERE "TenantSlug" = $1 AND "Id" = $2
            """, r => new { title = r.GetString(0), status = r.GetString(1), dueDate = r.Date(2), createdBy = r.Str(3), createdAt = r.GetFieldValue<DateTime>(4), closedAt = r.Ts(5) },
            ct, Tenant, id)).FirstOrDefault();
        if (head is null) return NotFound();
        var people = await PeopleMapAsync(ct);
        var items = await Db.QueryAsync($"SELECT {ItemCols} FROM governance_access_review_items WHERE \"TenantSlug\" = $1 AND \"ReviewId\" = $2", MapItem, ct, Tenant, id);
        return Ok(new { id, head.title, head.status, head.dueDate, head.createdBy, head.createdAt, head.closedAt,
            items = items.OrderBy(i => i.EmployeeId is { } e ? people.GetValueOrDefault(e)?.Name : i.AccountName).Select(i => Shape(i, people)) });
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var n = await Db.ExecuteAsync("""UPDATE governance_access_reviews SET "Status" = 'Closed', "ClosedAt" = now() WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" = 'Open'""", ct, Tenant, id);
        if (n == 0) return NotFound(new { message = "Açık kampanya bulunamadı." });
        await ComplianceAudit.WriteAsync(Db, Tenant, "AccessReview", id.ToString(), "Closed", new { }, Me.UserId, Me.Name, ct);
        return Ok(new { id, status = "Closed" });
    }

    [HttpPost("{id:guid}/remind")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Remind(Guid id, CancellationToken ct) => Ok(new { notified = await NotifyReviewersAsync(id, ct) });

    /// <summary>Kararı bekleyen satırların gözden geçirenlerine (yoksa kampanyayı açan İK'ya değil, İK onaycısına) bildirim.</summary>
    private async Task<int> NotifyReviewersAsync(Guid id, CancellationToken ct) => await AccessReviewJobs.NotifyPendingAsync(Db, Tenant, id, ct);

    /// <summary>
    /// "Kaldırılsın" kararını uygular: seçilen standart roller ve ek izinler tenant-service'in mevcut
    /// rol/izin uçlarıyla (çağıranın jetonu, kendi yetki kuralları — son yönetici kilidi dahil) kaldırılır.
    /// </summary>
    [HttpPost("{id:guid}/items/{itemId:guid}/apply")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Apply(Guid id, Guid itemId, CancellationToken ct)
    {
        var item = (await Db.QueryAsync($"SELECT {ItemCols} FROM governance_access_review_items WHERE \"TenantSlug\" = $1 AND \"ReviewId\" = $2 AND \"Id\" = $3",
            MapItem, ct, Tenant, id, itemId)).FirstOrDefault();
        if (item is null) return NotFound();
        if (item.Decision != "Remove") return BadRequest(new { message = "Bu satır için kaldırma kararı yok." });
        if (item.AppliedAt is not null) return Conflict(new { message = "Bu karar zaten uygulandı." });
        var results = new List<string>();
        var failed = 0;
        foreach (var r in item.RemoveRoles)
        {
            var isPermission = r.Contains(':');
            var (status, body) = isPermission
                ? await TenantApiAsync(HttpMethod.Delete, "/api/my-tenant/members/permissions", new { keycloakUserId = item.KeycloakUserId, permission = r }, ct)
                : await TenantApiAsync(HttpMethod.Delete, "/api/my-tenant/members/roles", new { keycloakUserId = item.KeycloakUserId, role = r }, ct);
            if (status is >= 200 and < 300) results.Add($"{r}: kaldırıldı");
            else
            {
                failed++;
                var msg = status == 403 ? "yetkiniz yok (ek izinleri yalnızca şirket yöneticisi kaldırır)" : ErrorMessage(body) ?? $"hata {status}";
                results.Add($"{r}: {msg}");
            }
        }
        var summary = string.Join("; ", results);
        await Db.ExecuteAsync("""
            UPDATE governance_access_review_items SET "AppliedAt" = CASE WHEN $4 THEN now() ELSE NULL END, "AppliedBy" = $5, "ApplyResult" = $6
            WHERE "TenantSlug" = $1 AND "ReviewId" = $2 AND "Id" = $3
            """, ct, Tenant, id, itemId, failed == 0, Me.Name, summary);
        await ComplianceAudit.WriteAsync(Db, Tenant, "AccessReviewItem", itemId.ToString(), "RolesRemoved",
            new { employeeId = item.EmployeeId, removed = item.RemoveRoles, failed, result = summary }, Me.UserId, Me.Name, ct);
        return Ok(new { applied = failed == 0, result = summary });
    }

    private static string? ErrorMessage(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var m) ? m.GetString() : null; }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ yönetici: kendi listesi

    /// <summary>
    /// Oturumdaki kişinin karar vereceği satırlar (açık kampanyalar): yöneticiye ekibi; İK'ya ayrıca
    /// gözden geçireni olmayan (departman başı bulunmayan) satırlar. KVKK: yalnızca rol/izin adı, ad ve pozisyon.
    /// </summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (!Me.IsManager) return Ok(new { items = Array.Empty<object>() });
        var me = await MyPersonAsync(ct);
        try
        {
            var items = await Db.QueryAsync($"""
                SELECT {string.Join(",", ItemCols.Split(',').Select(c => "i." + c.Trim()))}, r."Title", r."DueDate"
                FROM governance_access_review_items i JOIN governance_access_reviews r ON r."Id" = i."ReviewId"
                WHERE i."TenantSlug" = $1 AND r."Status" = 'Open'
                  AND (i."ReviewerEmployeeId" = $2 OR ($3 AND i."ReviewerEmployeeId" IS NULL))
                  AND i."EmployeeId" IS DISTINCT FROM $2 AND i."KeycloakUserId" <> $4
                """, r => (Item: MapItem(r), Title: r.GetString(ItemColCount), Due: r.Date(ItemColCount + 1)), ct, Tenant,
                (object?)me?.Id ?? DBNull.Value, Me.IsHr, Me.UserId);
            var people = await PeopleMapAsync(ct);
            return Ok(new
            {
                items = items.OrderBy(x => x.Item.Decision is not null).ThenBy(x => x.Item.EmployeeId is { } e ? people.GetValueOrDefault(e)?.Name : x.Item.AccountName)
                    .Select(x => new { review = x.Title, dueDate = x.Due, item = Shape(x.Item, people) }),
            });
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return Ok(new { items = Array.Empty<object>() });
        }
    }

    public record DecideInput(string Decision, List<string>? RemoveRoles, string? Note);

    [HttpPost("items/{itemId:guid}/decide")]
    public async Task<IActionResult> Decide(Guid itemId, [FromBody] DecideInput b, CancellationToken ct)
    {
        if (b.Decision is not ("Keep" or "Remove")) return BadRequest(new { message = "Karar 'uygun' ya da 'kaldırılsın' olmalı." });
        var me = await MyPersonAsync(ct);
        var row = (await Db.QueryAsync($"""
            SELECT {string.Join(",", ItemCols.Split(',').Select(c => "i." + c.Trim()))}, r."Status"
            FROM governance_access_review_items i JOIN governance_access_reviews r ON r."Id" = i."ReviewId"
            WHERE i."TenantSlug" = $1 AND i."Id" = $2
            """, r => (Item: MapItem(r), Status: r.GetString(ItemColCount)), ct, Tenant, itemId)).FirstOrDefault();
        if (row.Item is null) return NotFound();
        var item = row.Item;
        var mayDecide = (me is not null && item.ReviewerEmployeeId == me.Id) || (Me.IsHr && item.ReviewerEmployeeId is null);
        // Kişi kendi erişimini gözden geçiremez (çalışan kaydı olmayan hesapta Keycloak kimliğiyle karşılaştırılır).
        if (!mayDecide || (me is not null && item.EmployeeId == me.Id) || item.KeycloakUserId == Me.UserId)
            return StatusCode(403, new { message = "Bu kişinin erişimini gözden geçirme yetkiniz yok." });
        if (row.Status != "Open") return Conflict(new { message = "Kampanya kapandı." });
        if (item.AppliedAt is not null) return Conflict(new { message = "Bu karar uygulandı; değiştirilemez." });
        var allowed = item.Roles.Concat(item.Permissions).ToHashSet();
        var remove = b.Decision == "Remove"
            ? ((b.RemoveRoles is { Count: > 0 } sel ? sel : allowed.ToList()).Where(allowed.Contains).Distinct().ToArray())
            : Array.Empty<string>();
        if (b.Decision == "Remove" && remove.Length == 0) return BadRequest(new { message = "Kaldırılacak en az bir rol ya da izin seçin." });
        var note = string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim()[..Math.Min(500, b.Note.Trim().Length)];
        await Db.ExecuteAsync("""
            UPDATE governance_access_review_items SET "Decision" = $3, "RemoveRoles" = $4, "Note" = $5, "DecidedBy" = $6, "DecidedByName" = $7, "DecidedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, itemId, b.Decision, remove, note, Me.UserId, Me.Name);
        await ComplianceAudit.WriteAsync(Db, Tenant, "AccessReviewItem", itemId.ToString(), "Decided",
            new { employeeId = item.EmployeeId, decision = b.Decision, remove }, Me.UserId, Me.Name, ct);
        return Ok(new { itemId, decision = b.Decision, removeRoles = remove });
    }
}

/// <summary>Erişim gözden geçirme hatırlatmaları: açık kampanyada karar bekleyenlere 3 günde bir; 90 günü geçen kiracıda İK'ya yeni çeyrek hatırlatması.</summary>
public static class AccessReviewJobs
{
    public static async Task<int> NotifyPendingAsync(Sql db, string tenant, Guid reviewId, CancellationToken ct)
    {
        var reviewers = await db.QueryAsync("""
            SELECT DISTINCT "ReviewerEmployeeId" FROM governance_access_review_items
            WHERE "TenantSlug" = $1 AND "ReviewId" = $2 AND "Decision" IS NULL AND "ReviewerEmployeeId" IS NOT NULL
            """, r => r.GetGuid(0), ct, tenant, reviewId);
        var hrPending = await db.ScalarAsync("""
            SELECT 1 FROM governance_access_review_items WHERE "TenantSlug" = $1 AND "ReviewId" = $2 AND "Decision" IS NULL AND "ReviewerEmployeeId" IS NULL LIMIT 1
            """, ct, tenant, reviewId) is not null;
        var to = new HashSet<Guid>(reviewers);
        if (hrPending) foreach (var g in await SecuritySettingsStore.RecipientsAsync(db, tenant, await SecuritySettingsStore.LoadAsync(db, tenant, ct), ct)) to.Add(g);
        await db.ExecuteAsync("""UPDATE governance_access_reviews SET "LastReminderAt" = now() WHERE "TenantSlug" = $1 AND "Id" = $2""", ct, tenant, reviewId);
        return await BulkNotifyLocalized.InAppAsync(db, tenant, to,
            "Erişim gözden geçirme: kararınız bekleniyor", "Access review: your decision is needed",
            "Ekibinizdeki kişilerin rol ve izinlerini gözden geçirip her biri için 'uygun' ya da 'kaldırılsın' seçin (Yönetim › Erişim gözden geçirme).",
            "Review the roles and permissions of your team members and mark each as 'keep' or 'remove' (Administration › Access review).",
            "security.accessreview", ct);
    }

    public static async Task RunOnceAsync(Sql db, CancellationToken ct)
    {
        var open = await db.QueryAsync("""
            SELECT "TenantSlug","Id" FROM governance_access_reviews
            WHERE "Status" = 'Open' AND coalesce("LastReminderAt", "CreatedAt") < now() - interval '3 days'
            """, r => (T: r.GetString(0), Id: r.GetGuid(1)), ct);
        foreach (var (t, id) in open) await NotifyPendingAsync(db, t, id, ct);
        // Çeyreklik: son kampanyası 90 günü geçen (ve en az bir kampanya yapmış) kiracılarda İK'ya bir kez hatırlatma.
        var due = await db.QueryAsync("""
            SELECT r."TenantSlug", max(r."CreatedAt") FROM governance_access_reviews r
            GROUP BY r."TenantSlug" HAVING max(r."CreatedAt") < now() - interval '90 days'
               AND NOT EXISTS (SELECT 1 FROM governance_access_reviews o WHERE o."TenantSlug" = r."TenantSlug" AND o."Status" = 'Open')
               AND coalesce(max(r."QuarterReminderAt"), 'epoch') < max(r."CreatedAt") + interval '90 days'
            """, r => r.GetString(0), ct);
        foreach (var t in due)
        {
            var to = await SecuritySettingsStore.RecipientsAsync(db, t, await SecuritySettingsStore.LoadAsync(db, t, ct), ct);
            await BulkNotifyLocalized.InAppAsync(db, t, to, "Çeyreklik erişim gözden geçirme zamanı", "Quarterly access review is due",
                "Son erişim gözden geçirmesinin üzerinden 90 gün geçti. Veri koruma › Erişim gözden geçirme ekranından yeni kampanya başlatın.",
                "90 days have passed since the last access review. Start a new campaign under Data protection › Access review.",
                "security.accessreview", ct);
            await db.ExecuteAsync("""
                UPDATE governance_access_reviews SET "QuarterReminderAt" = now()
                WHERE "TenantSlug" = $1 AND "CreatedAt" = (SELECT max("CreatedAt") FROM governance_access_reviews WHERE "TenantSlug" = $1)
                """, ct, t);
        }
    }
}

/// <summary>Saatte bir erişim gözden geçirme hatırlatmalarını çalıştırır.</summary>
public sealed class AccessReviewWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AccessReviewWorker> _log;
    public AccessReviewWorker(IServiceProvider sp, ILogger<AccessReviewWorker> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<GovernanceService.Tenancy.TenantContext>().IsPlatformAdmin = true;
                await AccessReviewJobs.RunOnceAsync(scope.ServiceProvider.GetRequiredService<Sql>(), ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01") { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Erişim gözden geçirme hatırlatması hata verdi");
            }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}
