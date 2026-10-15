using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Tenancy;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y25 + G4: kayıtlı rapor, panoya sabitleme ve zamanlanmış teslim.
 * Rapor asistanı sorusu parametreleriyle saklanır; her açılışta GÜNCEL
 * yetkiyle yeniden çalışır (sonuç saklanmaz). Kayıtlı çalıştırmalarda
 * 5 kişiden küçük grupların değeri gizlenir. Kişi bazında rapor
 * zamanlanamaz. Zamanlanmış teslim yalnızca oturum açmayı gerektiren
 * BAĞLANTI içerir; bildirime veri/rakam yazılmaz.
 * ==================================================================== */
[Route("api/saved-reports")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class SavedReportsController : AppController
{
    public static readonly string[] Schedules = { "None", "Daily", "Weekly", "Monthly" };
    public const int MaxPinned = 6;

    public sealed record Row(Guid Id, string OwnerUserId, Guid? OwnerEmployeeId, string Name, string Question, string Lang, Guid? DepartmentId,
        DateOnly? FromDate, DateOnly? ToDate, bool? Compare, string Metric, string GroupBy, bool PersonLevel, bool Pinned, string Schedule,
        DateTime? NextRunAt, DateTime? LastRunAt, int DeliveryCount, DateTime CreatedAt, string ScheduleTime, int? ScheduleDay);

    private const string Cols = "\"Id\",\"OwnerUserId\",\"OwnerEmployeeId\",\"Name\",\"Question\",\"Lang\",\"DepartmentId\",\"FromDate\",\"ToDate\",\"Compare\",\"Metric\",\"GroupBy\",\"PersonLevel\",\"Pinned\",\"Schedule\",\"NextRunAt\",\"LastRunAt\",\"DeliveryCount\",\"CreatedAt\",\"ScheduleTime\",\"ScheduleDay\"";

    private static Row Map(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GuidOrNull(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GuidOrNull(6), r.Date(7), r.Date(8), r.IsDBNull(9) ? null : r.GetBoolean(9), r.GetString(10), r.GetString(11), r.GetBoolean(12), r.GetBoolean(13),
        r.GetString(14), r.Ts(15), r.Ts(16), r.GetInt32(17), r.GetFieldValue<DateTime>(18), r.GetString(19), r.IsDBNull(20) ? null : r.GetInt32(20));

    private Task<List<Row>> MineAsync(CancellationToken ct, Guid? id = null) =>
        Db.QueryAsync($"SELECT {Cols} FROM governance_saved_reports WHERE \"TenantSlug\" = $1 AND \"OwnerUserId\" = $2 AND ($3::uuid IS NULL OR \"Id\" = $3) ORDER BY \"Pinned\" DESC, \"CreatedAt\" DESC",
            Map, ct, Tenant, Me.UserId, (object?)id);

    private object View(Row r) => new
    {
        r.Id, r.Name, r.Question, r.DepartmentId, from = r.FromDate, to = r.ToDate, r.Compare, r.Metric, r.GroupBy, r.PersonLevel, r.Pinned,
        r.Schedule, time = r.ScheduleTime, day = r.ScheduleDay, timeZone = ReportSchedule.TimeZoneId,
        r.NextRunAt, r.LastRunAt, r.DeliveryCount, r.CreatedAt, schedulable = !r.PersonLevel,
        link = $"/panel/rapor-asistani?kayitli={r.Id}",
    };

    private NlReport.Options Opt(Row r) => new(r.DepartmentId, r.FromDate, r.ToDate, r.Compare, Suppress: true, AllowSalary: Me.IsHr);

    private Task<NlReport.Result> RunAsync(Row r, CancellationToken ct) =>
        NlReport.RunAsync(Db, Tenant, r.Question, Me.IsHr, ct, Lang, Opt(r));

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok((await MineAsync(ct)).Select(View));

    public record SaveInput(string Name, string Question, Guid? DepartmentId, DateOnly? From, DateOnly? To, bool? Compare, bool? Pinned, string? Schedule,
        string? Time = null, int? Day = null);

    private IActionResult? BadSchedule(string schedule, string? time, int? day)
    {
        if (!Schedules.Contains(schedule)) return BadRequest(new { message = L("Geçersiz zamanlama.", "Invalid schedule.") });
        if (time is not null && ReportSchedule.ParseTime(time) is null)
            return BadRequest(new { message = L("Saat SS:dd biçiminde olmalı (ör. 08:30).", "The time must be HH:mm (e.g. 08:30)."), code = "invalid_time" });
        if (day is not null && (schedule == "Weekly" ? day is < 1 or > 7 : schedule == "Monthly" ? day is < 1 or > 28 : true))
            return BadRequest(new { message = L("Gün: haftalıkta 1–7 (pazartesi = 1), aylıkta 1–28.", "Day: 1–7 for weekly (Monday = 1), 1–28 for monthly."), code = "invalid_day" });
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> Create(SaveInput body, CancellationToken ct)
    {
        var name = (body.Name ?? "").Trim();
        var question = (body.Question ?? "").Trim();
        if (name.Length is 0 or > 80) return BadRequest(new { message = L("Ad 1–80 karakter olmalı.", "The name must be 1–80 characters.") });
        if (question.Length is 0 or > 300) return BadRequest(new { message = L("Soru 1–300 karakter olmalı.", "The question must be 1–300 characters.") });
        var schedule = body.Schedule ?? "None";
        if (BadSchedule(schedule, body.Time, body.Day) is { } bad) return bad;
        var time = ReportSchedule.ParseTime(body.Time ?? ReportSchedule.DefaultTime)!.Value.ToString("HH:mm");
        if (Convert.ToInt32(await Db.ScalarAsync("SELECT count(*)::int FROM governance_saved_reports WHERE \"TenantSlug\" = $1 AND \"OwnerUserId\" = $2", ct, Tenant, Me.UserId)) >= 50)
            return StatusCode(429, new { message = L("En fazla 50 kayıtlı rapor tutulabilir.", "At most 50 saved reports are allowed.") });
        var result = await NlReport.RunAsync(Db, Tenant, question, Me.IsHr, ct, Lang,
            new NlReport.Options(body.DepartmentId, body.From, body.To, body.Compare, Suppress: true, AllowSalary: Me.IsHr));
        if (!result.Understood) return BadRequest(new { message = result.Interpretation, code = "not_understood" });
        if (result.PersonLevel && schedule != "None")
            return BadRequest(new { message = L("Kişi bazındaki raporlar zamanlanamaz; yalnızca toplu (departman/ay/tür) raporlar zamanlanabilir.",
                "Person-level reports cannot be scheduled; only aggregate (department/month/type) reports can."), code = "person_level_not_schedulable" });
        if (body.Pinned == true && Convert.ToInt32(await Db.ScalarAsync("SELECT count(*)::int FROM governance_saved_reports WHERE \"TenantSlug\" = $1 AND \"OwnerUserId\" = $2 AND \"Pinned\"", ct, Tenant, Me.UserId)) >= MaxPinned)
            return BadRequest(new { message = L($"Panoya en fazla {MaxPinned} rapor sabitlenebilir.", $"At most {MaxPinned} reports can be pinned."), code = "too_many_pinned" });
        var me = await MyPersonAsync(ct);
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_saved_reports ("Id","TenantSlug","OwnerUserId","OwnerEmployeeId","OwnerIsHr","Name","Question","Lang","DepartmentId","FromDate","ToDate","Compare",
                "Metric","GroupBy","PersonLevel","Pinned","Schedule","NextRunAt","ScheduleTime","ScheduleDay","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,now(),now())
            """, ct, id, Tenant, Me.UserId, me?.Id, Me.IsHr, name, question, Lang, body.DepartmentId, body.From, body.To, body.Compare,
            result.Metric, result.GroupBy, result.PersonLevel, body.Pinned ?? false, schedule, ReportSchedule.Next(schedule, DateTime.UtcNow, time, body.Day),
            time, body.Day);
        var row = (await MineAsync(ct, id)).First();
        return Ok(new { report = View(row), result });
    }

    public record UpdateInput(string? Name, bool? Pinned, string? Schedule, Guid? DepartmentId, DateOnly? From, DateOnly? To, bool? Compare, bool? ClearFilters,
        string? Time = null, int? Day = null);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateInput body, CancellationToken ct)
    {
        var r = (await MineAsync(ct, id)).FirstOrDefault();
        if (r is null) return NotFound();
        var name = body.Name?.Trim() ?? r.Name;
        if (name.Length is 0 or > 80) return BadRequest(new { message = L("Ad 1–80 karakter olmalı.", "The name must be 1–80 characters.") });
        var schedule = body.Schedule ?? r.Schedule;
        var day = body.Schedule is not null && body.Schedule != r.Schedule && body.Day is null ? null : body.Day ?? r.ScheduleDay;
        if (BadSchedule(schedule, body.Time, day) is { } bad) return bad;
        var time = body.Time is null ? r.ScheduleTime : ReportSchedule.ParseTime(body.Time)!.Value.ToString("HH:mm");
        if (r.PersonLevel && schedule != "None")
            return BadRequest(new { message = L("Kişi bazındaki raporlar zamanlanamaz; yalnızca toplu (departman/ay/tür) raporlar zamanlanabilir.",
                "Person-level reports cannot be scheduled; only aggregate (department/month/type) reports can."), code = "person_level_not_schedulable" });
        var pinned = body.Pinned ?? r.Pinned;
        if (pinned && !r.Pinned && Convert.ToInt32(await Db.ScalarAsync("SELECT count(*)::int FROM governance_saved_reports WHERE \"TenantSlug\" = $1 AND \"OwnerUserId\" = $2 AND \"Pinned\"", ct, Tenant, Me.UserId)) >= MaxPinned)
            return BadRequest(new { message = L($"Panoya en fazla {MaxPinned} rapor sabitlenebilir.", $"At most {MaxPinned} reports can be pinned."), code = "too_many_pinned" });
        var clear = body.ClearFilters == true;
        var next = schedule == r.Schedule && time == r.ScheduleTime && day == r.ScheduleDay ? r.NextRunAt : ReportSchedule.Next(schedule, DateTime.UtcNow, time, day);
        await Db.ExecuteAsync("""
            UPDATE governance_saved_reports SET "Name" = $3, "Pinned" = $4, "Schedule" = $5, "NextRunAt" = $6,
                "DepartmentId" = $7, "FromDate" = $8, "ToDate" = $9, "Compare" = $10, "ScheduleTime" = $11, "ScheduleDay" = $12, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, name, pinned, schedule, next,
            clear ? null : body.DepartmentId ?? r.DepartmentId, clear ? null : body.From ?? r.FromDate, clear ? null : body.To ?? r.ToDate, clear ? null : body.Compare ?? r.Compare, time, day);
        return Ok(View((await MineAsync(ct, id)).First()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var n = await Db.ExecuteAsync("DELETE FROM governance_saved_reports WHERE \"TenantSlug\" = $1 AND \"OwnerUserId\" = $2 AND \"Id\" = $3", ct, Tenant, Me.UserId, id);
        return n == 0 ? NotFound() : NoContent();
    }

    /// <summary>Kayıtlı raporu GÜNCEL veri ve yetkiyle çalıştırır (bildirimdeki bağlantı buraya gelir).</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> Run(Guid id, CancellationToken ct)
    {
        var r = (await MineAsync(ct, id)).FirstOrDefault();
        if (r is null) return NotFound();
        var result = await RunAsync(r, ct);
        if (result.Metric == "salary" && result.Understood)
            await ComplianceAudit.WriteAsync(Db, Tenant, "Report", r.Id.ToString(), "SensitiveViewed", new { field = "salaryDistribution", saved = true }, Me.UserId, Me.Name, ct);
        return Ok(new { report = View(r), result });
    }

    /// <summary>Pano bileşeni: sabitlenmiş raporların güncel sonucu.</summary>
    [HttpGet("pinned")]
    [Authorize]
    public async Task<IActionResult> Pinned(CancellationToken ct)
    {
        // Kiracısız oturum (platform yöneticisi) ya da yönetici olmayan: sabitlenmiş rapor yok.
        if (!Me.IsManager || string.IsNullOrEmpty(HttpContext.RequestServices.GetRequiredService<GovernanceService.Tenancy.ITenantContext>().TenantSlug))
            return Ok(Array.Empty<object>());
        var rows = (await MineAsync(ct)).Where(r => r.Pinned).Take(MaxPinned).ToList();
        var list = new List<object>();
        foreach (var r in rows)
        {
            var result = await RunAsync(r, ct);
            list.Add(new { report = View(r), result });
        }
        return Ok(list);
    }
}

/// <summary>
/// Zamanlama (Europe/Istanbul): günlük SS:dd, haftalık seçilen gün (varsayılan pazartesi),
/// aylık ayın seçilen günü (1–28, varsayılan 1'i). Varsayılan saat 07:00.
/// </summary>
public static class ReportSchedule
{
    public const string TimeZoneId = "Europe/Istanbul";
    public const string DefaultTime = "07:00";

    private static readonly TimeZoneInfo Zone = Find();
    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "Türkiye", "Türkiye"); } // 2016'dan beri sabit UTC+3
    }

    public static TimeOnly? ParseTime(string? s) =>
        s is not null && TimeOnly.TryParseExact(s.Trim(), new[] { "HH:mm", "H:mm", "HH:mm:ss" }, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? new TimeOnly(t.Hour, t.Minute) : null;

    /// <param name="day">Haftalık: 1–7 (pazartesi = 1); aylık: 1–28.</param>
    public static DateTime? Next(string schedule, DateTime nowUtc, string? time = null, int? day = null)
    {
        if (schedule is not ("Daily" or "Weekly" or "Monthly")) return null;
        var t = ParseTime(time) ?? new TimeOnly(7, 0);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Zone);
        var todayAt = local.Date.Add(t.ToTimeSpan());
        DateTime nextLocal;
        switch (schedule)
        {
            case "Daily":
                nextLocal = local < todayAt ? todayAt : todayAt.AddDays(1);
                break;
            case "Weekly":
                var target = (DayOfWeek)((day ?? 1) % 7); // 7 = pazar
                var days = ((int)target - (int)local.DayOfWeek + 7) % 7;
                if (days == 0 && local >= todayAt) days = 7;
                nextLocal = todayAt.AddDays(days);
                break;
            default:
                var d = Math.Clamp(day ?? 1, 1, 28);
                var thisMonth = new DateTime(local.Year, local.Month, d).Add(t.ToTimeSpan());
                nextLocal = local < thisMonth ? thisMonth : thisMonth.AddMonths(1);
                break;
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), Zone);
    }
}

/// <summary>
/// Zamanlanmış rapor teslimi. Bildirim (uygulama içi + e-posta) yalnızca rapor adını ve
/// oturum açmayı gerektiren bağlantıyı içerir; veri, rakam ya da ek GÖNDERİLMEZ.
/// Aralık: SAVED_REPORTS_INTERVAL_SECONDS (varsayılan 300; testte kısa).
/// </summary>
public sealed class SavedReportWorker(IServiceProvider sp, ILogger<SavedReportWorker> log) : BackgroundService
{
    private static readonly int IntervalSeconds = int.TryParse(Environment.GetEnvironmentVariable("SAVED_REPORTS_INTERVAL_SECONDS"), out var s) && s > 0 ? s : 300;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(20, IntervalSeconds)), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Zamanlanmış rapor turu hata verdi"); }
            await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct);
        }
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
        var sql = scope.ServiceProvider.GetRequiredService<Sql>();
        var notifier = scope.ServiceProvider.GetRequiredService<Notifier>();
        var due = await sql.QueryAsync("""
            SELECT s."Id", s."TenantSlug", s."OwnerEmployeeId", s."Name", s."Schedule", s."PersonLevel", s."NextRunAt", e."Status", s."ScheduleTime", s."ScheduleDay"
            FROM governance_saved_reports s LEFT JOIN employee_employees e ON e."Id" = s."OwnerEmployeeId"
            WHERE s."Schedule" <> 'None' AND s."NextRunAt" IS NOT NULL AND s."NextRunAt" <= now()
            ORDER BY s."NextRunAt" LIMIT 200
            """, r => (Id: r.GetGuid(0), Tenant: r.GetString(1), Owner: r.GuidOrNull(2), Name: r.GetString(3), Schedule: r.GetString(4),
                Person: r.GetBoolean(5), Next: r.GetFieldValue<DateTime>(6), Status: r.Str(7), Time: r.GetString(8), Day: r.IsDBNull(9) ? (int?)null : r.GetInt32(9)), ct);
        var delivered = 0;
        foreach (var d in due)
        {
            // Sahibi ayrıldıysa ya da kişi bazında kayıt zamanlanmışsa teslim durdurulur.
            if (d.Owner is null || d.Status is null or "Terminated" || d.Person)
            {
                await sql.ExecuteAsync("UPDATE governance_saved_reports SET \"Schedule\" = 'None', \"NextRunAt\" = NULL, \"UpdatedAt\" = now() WHERE \"Id\" = $1", ct, d.Id);
                continue;
            }
            // Birden çok kopya çalışıyorsa aynı teslimi bir kez yap (iyimser kilit).
            var claimed = await sql.ExecuteAsync("""
                UPDATE governance_saved_reports SET "NextRunAt" = $2, "LastRunAt" = now(), "DeliveryCount" = "DeliveryCount" + 1
                WHERE "Id" = $1 AND "NextRunAt" = $3
                """, ct, d.Id, ReportSchedule.Next(d.Schedule, DateTime.UtcNow, d.Time, d.Day), d.Next);
            if (claimed == 0) continue;
            var url = $"{ChatService.PublicOrigin}/panel/rapor-asistani?kayitli={d.Id}";
            var subjectTr = $"Zamanlanmış rapor hazır: {d.Name}";
            var subjectEn = $"Scheduled report ready: {d.Name}";
            var bodyTr = $"Güncel sonuçları görmek için HR360'a giriş yapın: {url}\nGüvenlik ve KVKK gereği rapor verisi bildirime ya da e-postaya eklenmez.";
            var bodyEn = $"Sign in to HR360 to see the current results: {url}\nFor security and data protection, report data is never included in notifications or emails.";
            await notifier.LocalizedAsync(d.Tenant, d.Owner.Value, subjectTr, subjectEn, bodyTr, bodyEn, "report.scheduled", ct, "InApp", url, "Raporu aç", "Open report");
            await notifier.LocalizedAsync(d.Tenant, d.Owner.Value, subjectTr, subjectEn, bodyTr, bodyEn, "report.scheduled", ct, "Email", url, "Raporu aç", "Open report");
            delivered++;
        }
        return delivered;
    }
}
