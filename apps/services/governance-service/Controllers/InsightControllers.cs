using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;
using GovernanceService.Tenancy;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Denetim kaydı okuyucusu. Tüm servislerin AuditInterceptor'ı ortak
 * audit_log tablosuna yazar; burası filtreler, istek kimliğine göre
 * gruplar ve CSV dışa aktarır.
 * ==================================================================== */
[Route("api/audit")]
[Authorize(Policy = "RequireHrAdmin")]
public class AuditController : AppController
{
    private (string Where, List<object?> Args) Filter(string? service, string? entityType, string? entityId, string? userId,
        string? action, string? q, DateTime? from, DateTime? to)
    {
        var where = new List<string>();
        var args = new List<object?>();
        void Add(string clause, object? value) { args.Add(value); where.Add(clause.Replace("@", "$" + args.Count)); }
        var tc = HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (!(tc.IsPlatformAdmin && string.IsNullOrEmpty(tc.TenantSlug))) Add("\"TenantSlug\" = @", Tenant);
        if (!string.IsNullOrEmpty(service)) Add("\"Service\" = @", service);
        if (!string.IsNullOrEmpty(entityType)) Add("\"EntityType\" = @", entityType);
        if (!string.IsNullOrEmpty(entityId)) Add("\"EntityId\" = @", entityId);
        if (!string.IsNullOrEmpty(userId)) Add("\"UserId\" = @", userId);
        if (!string.IsNullOrEmpty(action)) Add("\"Action\" = @", action);
        if (!string.IsNullOrEmpty(q)) Add("(\"Changes\"::text ILIKE @ OR \"UserName\" ILIKE @ OR \"EntityId\" ILIKE @)", $"%{q}%");
        if (from is not null) Add("\"OccurredAt\" >= @", DateTime.SpecifyKind(from.Value, DateTimeKind.Utc));
        if (to is not null) Add("\"OccurredAt\" < @", DateTime.SpecifyKind(to.Value, DateTimeKind.Utc));
        return (where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where), args);
    }

    private static object Map(Npgsql.NpgsqlDataReader r) => new
    {
        id = r.GetInt64(0), service = r.GetString(1), entityType = r.GetString(2), entityId = r.Str(3), action = r.GetString(4),
        changes = r.IsDBNull(5) ? (JsonElement?)null : JsonDocument.Parse(r.GetString(5)).RootElement.Clone(),
        userId = r.Str(6), userName = r.Str(7), correlationId = r.Str(8), ipAddress = r.Str(9), occurredAt = r.GetFieldValue<DateTime>(10),
    };

    private const string Cols = "\"Id\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\"::text,\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\"";

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? service, [FromQuery] string? entityType, [FromQuery] string? entityId,
        [FromQuery] string? userId, [FromQuery] string? action, [FromQuery] string? q, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var (where, args) = Filter(service, entityType, entityId, userId, action, q, from, to);
        pageSize = Math.Clamp(pageSize, 10, 200);
        page = Math.Max(1, page);
        var total = Convert.ToInt64(await Db.ScalarAsync($"SELECT count(*) FROM audit_log {where}", ct, args.ToArray()));
        var items = await Db.QueryAsync($"SELECT {Cols} FROM audit_log {where} ORDER BY \"OccurredAt\" DESC, \"Id\" DESC LIMIT {pageSize} OFFSET {(page - 1) * pageSize}", Map, ct, args.ToArray());
        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>
    /// G21: Kiracının denetim zincirini doğrular. Her satırın özeti yeniden hesaplanır,
    /// önceki satırın özetine bağlılığı ve sıra numarasının kesintisizliği denetlenir.
    /// Saklama süresi dolan en eski kayıtların silinmesi zincirin başını kısaltır (hata değil).
    /// </summary>
    [HttpGet("verify")]
    public async Task<IActionResult> Verify(CancellationToken ct)
    {
        var x = await AuditChainGuard.VerifyAsync(Db, Tenant, ct);
        var anchorProblem = await AuditChainGuard.CheckAnchorAsync(Db, Tenant, x, ct);
        var ok = x.Ok && anchorProblem is null;
        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
            VALUES ($1,'governance-service','AuditChain',NULL,'Verified',$2::jsonb,$3,$4,now())
            """, ct, Tenant, JsonSerializer.Serialize(new { ok, x.Rows }), Me.UserId, Me.Name);
        // Gecelik otomatik kontrolün son sonucu (AuditChainGuard).
        var nightly = (await Db.QueryAsync("""
            SELECT "CheckedAt", "Ok", "ToSeq", "Problem" FROM governance_audit_anchors
             WHERE "TenantSlug" = $1 ORDER BY "CheckedAt" DESC LIMIT 1
            """, r => new { checkedAt = r.GetFieldValue<DateTime>(0), ok = r.GetBoolean(1), toSeq = r.IsDBNull(2) ? (long?)null : r.GetInt64(2), problem = r.Str(3) }, ct, Tenant)).FirstOrDefault();
        return Ok(new
        {
            ok, rows = x.Rows, tampered = x.Tampered, broken = x.Broken, gaps = x.Gaps, unchained = x.Unchained,
            firstProblemSeq = x.FirstProblemSeq, fromSeq = x.FromSeq, toSeq = x.ToSeq, head = x.Head,
            anchorProblem, nightly, checkedAt = DateTime.UtcNow,
        });
    }

    /// <summary>SIEM aktarımının durumu (yapılandırma ve son gönderim).</summary>
    [HttpGet("siem")]
    public async Task<IActionResult> SiemStatus(CancellationToken ct)
    {
        var st = await Db.QueryAsync("""SELECT "LastAuditId","LastSentAt","Sent","LastError" FROM governance_siem_cursor WHERE "Id" = 1""",
            r => new { lastAuditId = r.GetInt64(0), lastSentAt = r.IsDBNull(1) ? (DateTime?)null : r.GetFieldValue<DateTime>(1), sent = r.GetInt64(2), lastError = r.Str(3) }, ct);
        return Ok(new { configured = SiemExporter.Endpoint is not null, endpoint = SiemExporter.EndpointDisplay, state = st.FirstOrDefault() });
    }

    [HttpGet("facets")]
    public async Task<IActionResult> Facets(CancellationToken ct)
    {
        var (where, args) = Filter(null, null, null, null, null, null, DateTime.UtcNow.AddDays(-90), null);
        var services = await Db.QueryAsync($"SELECT \"Service\", count(*) FROM audit_log {where} GROUP BY 1 ORDER BY 2 DESC", r => new { name = r.GetString(0), count = r.GetInt64(1) }, ct, args.ToArray());
        var types = await Db.QueryAsync($"SELECT \"EntityType\", count(*) FROM audit_log {where} GROUP BY 1 ORDER BY 2 DESC LIMIT 40", r => new { name = r.GetString(0), count = r.GetInt64(1) }, ct, args.ToArray());
        var users = await Db.QueryAsync($"SELECT \"UserId\", max(\"UserName\"), count(*) FROM audit_log {where} GROUP BY 1 ORDER BY 3 DESC LIMIT 30", r => new { id = r.Str(0), name = r.Str(1), count = r.GetInt64(2) }, ct, args.ToArray());
        var daily = await Db.QueryAsync($"SELECT date_trunc('day', \"OccurredAt\")::date, count(*) FROM audit_log {where} GROUP BY 1 ORDER BY 1", r => new { day = r.GetFieldValue<DateOnly>(0), count = r.GetInt64(1) }, ct, args.ToArray());
        return Ok(new { services, entityTypes = types, users, daily });
    }

    [HttpGet("correlation/{id}")]
    public async Task<IActionResult> ByCorrelation(string id, CancellationToken ct)
    {
        var (where, args) = Filter(null, null, null, null, null, null, null, null);
        args.Add(id);
        var clause = (where.Length == 0 ? "WHERE " : where + " AND ") + $"\"CorrelationId\" = ${args.Count}";
        return Ok(await Db.QueryAsync($"SELECT {Cols} FROM audit_log {clause} ORDER BY \"OccurredAt\"", Map, ct, args.ToArray()));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? service, [FromQuery] string? entityType, [FromQuery] string? userId,
        [FromQuery] string? q, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var (where, args) = Filter(service, entityType, null, userId, null, q, from, to);
        var rows = await Db.QueryAsync($"SELECT {Cols} FROM audit_log {where} ORDER BY \"OccurredAt\" DESC LIMIT 50000",
            r => new[] { r.GetFieldValue<DateTime>(10).ToString("u"), r.GetString(1), r.GetString(2), r.Str(3) ?? "", r.GetString(4), r.Str(7) ?? r.Str(6) ?? "", r.Str(8) ?? "", r.Str(9) ?? "", r.Str(5) ?? "" },
            ct, args.ToArray());
        // Güvenlik dalgası 2B: indirene ait filigran satırı + iz kodu (denetim kaydına bağlı).
        var trace = Watermark.NewCode();
        var at = DateTimeOffset.UtcNow;
        // Platform yöneticisinin kiracısız oturumunda satır platform düzeyinde (TenantSlug NULL) kalır.
        await Watermark.AuditAsync(Db, HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantSlug!, "audit-log-csv", null, "csv", rows.Count, trace, Me.UserId, Me.Name, ct);
        var sb = new StringBuilder("﻿# " + Watermark.Text(Me.Name, at, trace).Replace(";", ",") + "\n");
        sb.Append("Zaman;Servis;Varlık;Kimlik;İşlem;Kullanıcı;İstek kimliği;IP;Değişiklik\n");
        foreach (var row in rows) sb.AppendLine(string.Join(';', row.Select(c => "\"" + c.Replace("\"", "\"\"") + "\"")));
        Response.Headers["X-HR360-Trace"] = trace;
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"denetim-kaydi-{DateTime.UtcNow:yyyyMMdd}.csv");
    }
}

/* ======================================================================
 * Canlı olay radarı: Kafka'dan gelen olaylar Server-Sent Events ile.
 * Tarayıcı EventSource başlık gönderemediği için istemci fetch akışı
 * kullanır (Authorization başlığıyla); nginx tamponlamayı kapatır.
 * ==================================================================== */
// GÜVENLİK/KVKK: olay yükleri kiracı genelinde kişisel veri taşır (işe alım, görev, izin,
// onay talepleri); önceden yöneticilere açıktı. Yalnızca İK.
[Route("api/events")]
[Authorize(Policy = "RequireHrAdmin")]
public class EventsController : AppController
{
    private readonly EventHub _hub;
    public EventsController(EventHub hub) => _hub = hub;

    private bool AllTenants
    {
        get
        {
            var tc = HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tc.IsPlatformAdmin && string.IsNullOrEmpty(tc.TenantSlug);
        }
    }

    [HttpGet("recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 100, [FromQuery] string? type = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var args = new List<object?>();
        var where = new List<string>();
        if (!AllTenants) { args.Add(Tenant); where.Add($"\"TenantSlug\" = ${args.Count}"); }
        if (!string.IsNullOrEmpty(type)) { args.Add(type); where.Add($"\"EventType\" = ${args.Count}"); }
        var w = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var rows = await Db.QueryAsync(
            $"SELECT \"Id\", \"TenantSlug\", \"Topic\", \"EventType\", \"Payload\"::text, \"OccurredAt\" FROM governance_events {w} ORDER BY \"OccurredAt\" DESC LIMIT {limit}",
            r =>
            {
                // Eski kayıtlar için de (göç öncesi) gizli alanlar okurken ayıklanır.
                JsonElement? p = r.IsDBNull(4) ? null
                    : EventHub.Sanitize(JsonDocument.Parse(r.GetString(4)).RootElement.Clone(), EventHub.RadarPersonalFields);
                var type = r.GetString(3);
                return new RadarEvent(r.GetGuid(0), r.Str(1), r.GetString(2), type, p, r.GetFieldValue<DateTime>(5), EventHub.Describe(type, p));
            }, ct, args.ToArray());
        return Ok(rows);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var args = AllTenants ? Array.Empty<object?>() : new object?[] { Tenant };
        var tw = AllTenants ? "" : "AND \"TenantSlug\" = $1";
        var byType = await Db.QueryAsync($"SELECT \"EventType\", count(*) FROM governance_events WHERE \"OccurredAt\" > now() - interval '7 days' {tw} GROUP BY 1 ORDER BY 2 DESC",
            r => new { type = r.GetString(0), count = r.GetInt64(1) }, ct, args);
        var hourly = await Db.QueryAsync($"SELECT date_trunc('hour', \"OccurredAt\"), count(*) FROM governance_events WHERE \"OccurredAt\" > now() - interval '24 hours' {tw} GROUP BY 1 ORDER BY 1",
            r => new { hour = r.GetFieldValue<DateTime>(0), count = r.GetInt64(1) }, ct, args);
        return Ok(new { byType, hourly, listeners = _hub.Subscribers });
    }

    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        var tenant = AllTenants ? null : Tenant;
        var (id, reader) = _hub.Subscribe();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            await Response.WriteAsync("retry: 3000\n: hr360 olay akışı\n\n", ct);
            await Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                beat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var e = await reader.ReadAsync(beat.Token);
                    if (tenant is not null && e.TenantSlug != tenant) continue;
                    await Response.WriteAsync($"event: hr\nid: {e.Id}\ndata: {JsonSerializer.Serialize(e, json)}\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Response.WriteAsync(": ping\n\n", ct);
                }
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { _hub.Unsubscribe(id); }
    }
}

/* ======================================================================
 * Zaman makinesi: organizasyon geçmiş bir tarihte nasıldı? Görevlendirmelerin
 * EffectiveFrom/To aralıklarından o güne ait kadro yeniden kurulur.
 * ==================================================================== */
[Route("api/time-machine")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class TimeMachineController : AppController
{
    /// <summary>Ayrılış tarihi: Terminated ise son görevlendirmenin bitişi.</summary>
    public const string ExitDateSql = """
        CASE WHEN e."Status" = 'Terminated'
             THEN coalesce((SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id"), e."CreatedAt"::date)
        END
        """;

    [HttpGet]
    public async Task<IActionResult> Snapshot([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await Db.QueryAsync($"""
            SELECT e."Id", e."FirstName" || ' ' || e."LastName", e."HireDate", a."PositionTitle", d."Id", d."Name", d."HeadEmployeeId" = e."Id"
            FROM employee_employees e
            LEFT JOIN LATERAL (
                SELECT x."PositionTitle", x."DepartmentId" FROM employee_assignments x
                WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= $2 AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= $2)
                ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
            LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
            WHERE e."TenantSlug" = $1 AND e."HireDate" <= $2
              AND coalesce({ExitDateSql}, '9999-12-31'::date) >= $2
            ORDER BY 6 NULLS LAST, 2
            """, r => new
        {
            employeeId = r.GetGuid(0), name = r.GetString(1), hireDate = r.GetFieldValue<DateOnly>(2), position = r.Str(3),
            departmentId = r.GuidOrNull(4), department = r.Str(5) ?? "Atanmamış", isHead = !r.IsDBNull(6) && r.GetBoolean(6),
        }, ct, Tenant, day);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nowCount = Convert.ToInt32(await Db.ScalarAsync($"""
            SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND e."HireDate" <= $2
              AND coalesce({ExitDateSql}, '9999-12-31'::date) >= $2
            """, ct, Tenant, today));
        var changes = await Db.QueryAsync("""
            SELECT "EntityType", "Action", count(*) FROM audit_log
            WHERE "TenantSlug" = $1 AND "OccurredAt" >= $2 GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 12
            """, r => new { entityType = r.GetString(0), action = r.GetString(1), count = r.GetInt64(2) }, ct, Tenant,
            day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        // O tarihte var olan departmanlar (organizasyon şeması zaman kaydırıcısı): o güne kadar kurulmuş
        // ya da o gün içinde kişi bulunan. Silinmiş departmanların geçmişi tutulmadığı için görünmez.
        var existing = await Db.QueryAsync("""
            SELECT d."Id" FROM organization_departments d
            WHERE d."TenantSlug" = $1 AND (d."CreatedAt"::date <= $2 OR d."Id" = ANY($3))
            """, r => r.GetGuid(0), ct, Tenant, day, rows.Where(r => r.departmentId != null).Select(r => r.departmentId!.Value).Distinct().ToArray());

        return Ok(new
        {
            date = day, headcount = rows.Count, headcountToday = nowCount,
            departmentIds = existing,
            departments = rows.GroupBy(r => (r.departmentId, r.department)).Select(g => new
            {
                departmentId = g.Key.departmentId, department = g.Key.department, count = g.Count(), head = g.FirstOrDefault(x => x.isHead)?.name,
                people = g.Select(x => new { x.employeeId, x.name, x.position, x.hireDate, x.isHead }),
            }).OrderByDescending(g => g.count),
            changesSince = changes,
        });
    }

    [HttpGet("timeline")]
    public async Task<IActionResult> Timeline([FromQuery] int months = 24, CancellationToken ct = default)
    {
        months = Math.Clamp(months, 3, 120);
        var series = await Db.QueryAsync($"""
            SELECT m::date,
                (SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND e."HireDate" <= (m + interval '1 month - 1 day')::date
                   AND coalesce({ExitDateSql}, '9999-12-31'::date) >= (m + interval '1 month - 1 day')::date),
                (SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND date_trunc('month', e."HireDate") = m),
                (SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND date_trunc('month', {ExitDateSql}) = m)
            FROM generate_series(date_trunc('month', now()) - make_interval(months => $2 - 1), date_trunc('month', now()), interval '1 month') m
            ORDER BY 1
            """, r => new { month = r.GetFieldValue<DateOnly>(0), headcount = r.GetInt64(1), hires = r.GetInt64(2), exits = r.GetInt64(3) },
            ct, Tenant, months);
        var first = await Db.ScalarAsync("SELECT min(\"HireDate\") FROM employee_employees WHERE \"TenantSlug\" = $1", ct, Tenant);
        return Ok(new { series, earliest = first });
    }
}

/* ======================================================================
 * Analitik: operasyonel tabloların üstündeki analytics_* görünümlerinden
 * (hafif veri ambarı) çalışan sayısı, devir, izin, mesai ve maliyet eğilimi.
 * ==================================================================== */
[Route("api/analytics")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class AnalyticsController : AppController
{
    /// <summary>Kiracı geneli özet; aylık eğilim olduğu için 2 dk önbellekte tutulur (Redis varsa).</summary>
    [HttpGet("overview")]
    public Task<IActionResult> Overview([FromQuery] int months = 12, CancellationToken ct = default)
    {
        months = Math.Clamp(months, 3, 36);
        return HttpContext.RequestServices.GetRequiredService<AppCache>().JsonAsync(HttpContext, "analytics-overview", Tenant,
            $"{months}:{DateTime.UtcNow:yyyyMMdd}", TimeSpan.FromMinutes(2), c => BuildOverviewAsync(months, c), ct);
    }

    /// <summary>Küçük grup gizleme: MinGroup altındaki gruplar "Diğer"de toplanır; toplam da küçükse
    /// hiç döndürülmez. İkinci değer gizlenen kişi sayısıdır (arayüz not düşer).</summary>
    internal static (List<(string name, long n)> groups, long hidden) SuppressSmallGroups(IEnumerable<(string name, long n)> rows)
    {
        var list = rows.ToList();
        var shown = list.Where(r => r.n >= NlReport.MinGroup).ToList();
        var small = list.Where(r => r.n > 0 && r.n < NlReport.MinGroup).Sum(r => r.n);
        if (small >= NlReport.MinGroup) { shown.Add(("Diğer", small)); small = 0; }
        return (shown, small);
    }

    private async Task<object> BuildOverviewAsync(int months, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-(months - 1));
        since = new DateOnly(since.Year, since.Month, 1);

        var leave = await Db.QueryAsync("""
            SELECT month, leave_type, sum(days), sum(requests) FROM analytics_leave_monthly
            WHERE tenant_slug = $1 AND month >= $2 AND month <= date_trunc('month', now())::date AND status = 'Approved' GROUP BY 1, 2 ORDER BY 1
            """, r => new { month = r.GetFieldValue<DateOnly>(0), type = r.GetString(1), days = r.Dec(2) ?? 0, requests = r.GetInt64(3) }, ct, Tenant, since);
        var overtime = await Db.QueryAsync("""
            SELECT month, worked_minutes, overtime_minutes FROM analytics_overtime_monthly
            WHERE tenant_slug = $1 AND month >= $2 ORDER BY 1
            """, r => new { month = r.GetFieldValue<DateOnly>(0), workedHours = Math.Round(Convert.ToDouble(r.GetValue(1)) / 60, 1), overtimeHours = Math.Round(Convert.ToDouble(r.GetValue(2)) / 60, 1) }, ct, Tenant, since);
        var departmentRows = await Db.QueryAsync("SELECT department, headcount FROM analytics_department_headcount WHERE tenant_slug = $1 ORDER BY 2 DESC",
            r => (name: r.GetString(0), n: r.GetInt64(1)), ct, Tenant);
        var tenureRows = await Db.QueryAsync("""
            SELECT CASE WHEN age < 1 THEN '0-1 yıl' WHEN age < 3 THEN '1-3 yıl' WHEN age < 5 THEN '3-5 yıl' WHEN age < 10 THEN '5-10 yıl' ELSE '10+ yıl' END, count(*)
            FROM (SELECT extract(epoch FROM age(current_date, "HireDate")) / 31557600 AS age FROM employee_employees
                  WHERE "TenantSlug" = $1 AND "Status" <> 'Terminated') x GROUP BY 1 ORDER BY min(age)
            """, r => (name: r.GetString(0), n: r.GetInt64(1)), ct, Tenant);
        // KVKK: 5 kişiden küçük gruplar tek tek gösterilmez; "Diğer" altında birleşir, o da küçükse gizlenir.
        var (deptGroups, deptHidden) = SuppressSmallGroups(departmentRows);
        var (tenureGroups, tenureHidden) = SuppressSmallGroups(tenureRows);
        var departments = deptGroups.Select(g => new { department = g.name, headcount = g.n }).ToList();
        var tenure = tenureGroups.Select(g => new { bucket = g.name, count = g.n }).ToList();
        var expense = await Db.QueryAsync("""
            SELECT date_trunc('month', coalesce("SubmittedAt", "CreatedAt"))::date, sum("TotalAmount"), count(*) FROM expense_claims
            WHERE "TenantSlug" = $1 AND "Status" IN ('Approved','Paid') AND coalesce("SubmittedAt", "CreatedAt") >= $2 GROUP BY 1 ORDER BY 1
            """, r => new { month = r.GetFieldValue<DateOnly>(0), amount = r.Dec(1) ?? 0, claims = r.GetInt64(2) }, ct, Tenant, since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var timeline = await Db.QueryAsync($"""
            SELECT m::date,
                (SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND e."HireDate" <= (m + interval '1 month - 1 day')::date
                   AND coalesce({TimeMachineController.ExitDateSql}, '9999-12-31'::date) >= (m + interval '1 month - 1 day')::date),
                (SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND date_trunc('month', e."HireDate") = m),
                (SELECT count(*) FROM employee_employees e WHERE e."TenantSlug" = $1 AND date_trunc('month', {TimeMachineController.ExitDateSql}) = m)
            FROM generate_series($2::date, date_trunc('month', now()), interval '1 month') m ORDER BY 1
            """, r => new { month = r.GetFieldValue<DateOnly>(0), headcount = r.GetInt64(1), hires = r.GetInt64(2), exits = r.GetInt64(3) }, ct, Tenant, since);

        var exits = timeline.Sum(t => t.exits);
        var avgHead = timeline.Count == 0 ? 0 : timeline.Average(t => t.headcount);
        return new
        {
            months, timeline, leave, overtime, departments, tenure, expense,
            hiddenPeople = new { departments = deptHidden, tenure = tenureHidden },
            minGroup = NlReport.MinGroup,
            kpis = new
            {
                headcount = timeline.LastOrDefault()?.headcount ?? 0,
                hires = timeline.Sum(t => t.hires), exits,
                turnoverPercent = avgHead == 0 ? 0 : Math.Round(100.0 * exits / avgHead, 1),
                leaveDays = leave.Sum(l => l.days),
                overtimeHours = overtime.Sum(o => o.overtimeHours),
                expenseTotal = expense.Sum(e => e.amount),
            },
        };
    }
}
