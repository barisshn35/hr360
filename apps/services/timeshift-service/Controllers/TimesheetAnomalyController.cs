using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

/// <summary>
/// Puantaj denetimi (ML dalgası 2, madde 42): son haftalarda çalışanın KENDİ olağan haftalarına göre ani
/// saat artışı, 45 saatlik haftalık sınırın aşılması ve tekrarlayan eksik giriş/çıkış kaydı (ml-inference
/// /payroll/timesheet-anomaly). Yalnızca İK görür; bilgilendirme amaçlıdır — otomatik kesinti, yaptırım
/// ya da kayıt değişikliği yoktur. ml-inference'a çalışan kimliği gitmez (kiracıya özgü takma ad).
/// </summary>
[ApiController]
[Route("api/timesheet-report/anomalies")]
[Authorize]
public class TimesheetAnomalyController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TimesheetAnomalyController> _log;
    private static readonly string MlUrl =
        (Environment.GetEnvironmentVariable("ML_INFERENCE_URL") ?? "http://ml-inference:8000").TrimEnd('/') + "/payroll/timesheet-anomaly";

    public TimesheetAnomalyController(TimeShiftDbContext db, ITenantContext tenant, IHttpClientFactory http, ILogger<TimesheetAnomalyController> log)
    {
        _db = db; _tenant = tenant; _http = http; _log = log;
    }

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-timeshift-manage");

    public sealed record EntryRow(Guid EmployeeId, DateOnly Date, int WorkedMinutes, bool HasIn, bool HasOut);
    public sealed record WeekRow(string Employee, string Week, double WorkedHours, int DaysWorked, int MissingPunches);

    public static string Pseudonym(string tenant, Guid id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}:{id:N}")))[..16].ToLowerInvariant();

    public static string IsoWeek(DateOnly d) =>
        $"{ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue)):D4}-W{ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)):D2}";

    /// <summary>
    /// Kayıtları çalışan × ISO hafta olarak toplar (saf; birim testli). Eksik giriş/çıkış: girişi olup
    /// çıkışı olmayan (ya da tersi) GEÇMİŞ gün; bugünün açık kaydı sayılmaz (çalışan hâlâ içeride olabilir).
    /// </summary>
    public static List<WeekRow> BuildWeeks(string tenant, IEnumerable<EntryRow> entries, DateOnly today) =>
        entries.GroupBy(e => (e.EmployeeId, Week: IsoWeek(e.Date)))
            .Select(g => new WeekRow(
                Pseudonym(tenant, g.Key.EmployeeId), g.Key.Week,
                Math.Round(g.Sum(e => Math.Max(0, e.WorkedMinutes)) / 60.0, 2),
                g.Where(e => e.WorkedMinutes > 0 || e.HasIn).Select(e => e.Date).Distinct().Count(),
                g.Count(e => e.Date < today && e.HasIn != e.HasOut)))
            .OrderBy(w => w.Employee).ThenBy(w => w.Week).ToList();

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int weeks = 12, [FromQuery] int recent = 2, CancellationToken ct = default)
    {
        if (!IsHr) return Forbid();
        weeks = Math.Clamp(weeks, 6, 52);
        recent = Math.Clamp(recent, 1, 4);
        var tenant = _tenant.TenantSlug ?? "";
        var today = ClockCore.WorkDate(DateTimeOffset.UtcNow);
        var from = today.AddDays(-7 * weeks);
        var entries = await _db.TimeEntries.AsNoTracking().Where(e => e.Date >= from && e.Date <= today)
            .Select(e => new EntryRow(e.EmployeeId, e.Date, e.WorkedMinutes, e.ClockIn != null, e.ClockOut != null))
            .ToListAsync(ct);
        var ids = entries.Select(e => e.EmployeeId).Distinct().ToList();
        var byPseudo = ids.ToDictionary(id => Pseudonym(tenant, id));
        var rows = BuildWeeks(tenant, entries, today);
        if (rows.Count == 0) return Ok(new { available = true, items = Array.Empty<object>(), weeksEvaluated = Array.Empty<string>() });

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Post, MlUrl)
            {
                Content = JsonContent.Create(new
                {
                    weeks = rows.Select(r => new { employee = r.Employee, week = r.Week, worked_hours = Math.Min(168, r.WorkedHours),
                        days_worked = Math.Min(7, r.DaysWorked), missing_punches = Math.Min(14, r.MissingPunches) }),
                    recent,
                }),
            };
            req.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Puantaj denetimi yapılamadı: ML {Status}", (int)resp.StatusCode);
                return StatusCode(503, new { message = "Model servisine ulaşılamadı" });
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var items = new List<object>();
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!byPseudo.TryGetValue(it.GetProperty("employee").GetString() ?? "", out var emp)) continue;
                items.Add(new { employeeId = emp, week = it.GetProperty("week").GetString(), flags = it.GetProperty("flags").Clone() });
            }
            var evaluated = doc.RootElement.GetProperty("weeks_evaluated").EnumerateArray().Select(w => w.GetString()).ToList();
            return Ok(new { available = true, items, weeksEvaluated = evaluated, historyWeeks = weeks });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogWarning("Puantaj denetimi yapılamadı: {Error}", ex.Message);
            return StatusCode(503, new { message = "Model servisine ulaşılamadı" });
        }
    }
}
