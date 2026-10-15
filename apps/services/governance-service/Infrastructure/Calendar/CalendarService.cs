using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure.Calendar;

public sealed record MeetingRequest(
    string SourceType, Guid? SourceId, string Title, string? Description, DateTime StartsAtUtc, int DurationMinutes,
    string Provider, IReadOnlyList<Guid> ParticipantEmployeeIds, IReadOnlyList<string> ExternalEmails, bool AddToCalendars);

public sealed class CalendarFlowException(string message) : Exception(message);

/// <summary>
/// Takvim senkronizasyonu (onaylanan izinler → kişinin takvimi), toplantı oluşturma
/// (Zoom / Teams / Google Meet) ve boş/dolu sorgusu. Jetonlar SecretBox ile şifreli.
/// </summary>
public sealed class CalendarService
{
    private readonly Sql _sql;
    private readonly GoogleCalendar _google;
    private readonly MicrosoftCalendar _microsoft;
    private readonly ZoomApi _zoom;
    private readonly ILogger<CalendarService> _log;

    public CalendarService(Sql sql, GoogleCalendar google, MicrosoftCalendar microsoft, ZoomApi zoom, ILogger<CalendarService> log)
    { _sql = sql; _google = google; _microsoft = microsoft; _zoom = zoom; _log = log; }

    public ICalendarProvider Provider(string name) => name switch
    {
        "Google" => _google,
        "Microsoft" => _microsoft,
        _ => throw new CalendarFlowException($"Bilinmeyen takvim sağlayıcısı: {name}"),
    };

    public static string RedirectUri(string provider) => $"{ChatService.PublicOrigin}/api/governance/calendar/oauth/callback/{provider.ToLowerInvariant()}";

    public static Task<ProviderConfig?> ConfigAsync(GovernanceDbContext db, string tenant, string provider, CancellationToken ct) =>
        db.ProviderConfigs.FirstOrDefaultAsync(c => c.TenantSlug == tenant && c.Provider == provider && c.IsEnabled, ct);

    /// <summary>Geçerli erişim jetonu; süresi dolmak üzereyse yenilenir. Yenileme reddedilirse bağlantı "Error" olur.</summary>
    public async Task<string> AccessTokenAsync(GovernanceDbContext db, CalendarConnection conn, CancellationToken ct)
    {
        if (conn.ExpiresAt is { } exp && exp > DateTime.UtcNow.AddMinutes(2) && conn.AccessTokenEnc is not null)
            return SecretBox.Unprotect(conn.AccessTokenEnc)!;
        var cfg = await ConfigAsync(db, conn.TenantSlug, conn.Provider, ct)
            ?? throw new CalendarFlowException($"{conn.Provider} entegrasyonu yönetici tarafından kapatılmış.");
        var refresh = SecretBox.Unprotect(conn.RefreshTokenEnc) ?? throw new CalendarFlowException("Yenileme jetonu yok; takvimi yeniden bağlayın.");
        try
        {
            var t = await Provider(conn.Provider).RefreshAsync(cfg, refresh, ct);
            conn.AccessTokenEnc = SecretBox.Protect(t.AccessToken);
            if (t.RefreshToken is not null) conn.RefreshTokenEnc = SecretBox.Protect(t.RefreshToken);
            conn.ExpiresAt = DateTime.UtcNow.AddSeconds(t.ExpiresIn);
            conn.Status = "Active";
            conn.LastError = null;
            return t.AccessToken;
        }
        catch (ProviderApiException ex)
        {
            conn.Status = "Error";
            conn.LastError = $"Erişim yenilenemedi ({ex.Message}). Takvimi yeniden bağlayın.";
            throw new CalendarFlowException(conn.LastError);
        }
    }

    // ------------------------------------------------------------------ olaylar

    public async Task OnEventAsync(GovernanceDbContext db, string tenant, string type, JsonElement? payload, CancellationToken ct)
    {
        if (type != "leave.approved") return;
        if (!Guid.TryParse(EventHub.Field(payload, "LeaveRequestId"), out var leaveId) || !Guid.TryParse(EventHub.Field(payload, "EmployeeId"), out var empId)) return;
        var conns = await db.CalendarConnections.Where(c => c.TenantSlug == tenant && c.EmployeeId == empId && c.Status == "Active" && c.SyncLeaves).ToListAsync(ct);
        // KVKK m.9: dayanak kaydı olmayan yurt dışı hizmete veri gönderilmez.
        var allowed = await TransferGuard.AllowedAsync(db, tenant, ct);
        conns = conns.Where(c => allowed.Contains(TransferGuard.KeyOf(c.Provider))).ToList();
        if (conns.Count == 0) return;
        var leave = (await _sql.QueryAsync("""
            SELECT "Type", "StartDate", "EndDate", "Days" FROM leave_requests WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" = 'Approved'
            """, r => (Type: r.GetString(0), Start: r.GetFieldValue<DateOnly>(1), End: r.GetFieldValue<DateOnly>(2), Days: r.GetDecimal(3)), ct, tenant, leaveId)).FirstOrDefault();
        if (leave.Type is null) return;
        foreach (var c in conns)
        {
            if (await db.CalendarEventLinks.AnyAsync(l => l.ConnectionId == c.Id && l.SourceType == "leave" && l.SourceId == leaveId, ct)) continue;
            try
            {
                var token = await AccessTokenAsync(db, c, ct);
                var ev = await Provider(c.Provider).CreateEventAsync(token, new CalendarEventSpec(
                    $"İzinli — {ChatService.LeaveLabel(leave.Type)} izin", $"HR360 üzerinden onaylanan izin ({leave.Days:0.#} gün).",
                    null, null, leave.Start, leave.End, Array.Empty<Attendee>(), false, null, OutOfOffice: true), ct);
                db.CalendarEventLinks.Add(new CalendarEventLink { TenantSlug = tenant, ConnectionId = c.Id, SourceType = "leave", SourceId = leaveId, ExternalEventId = ev.Id });
                c.LastSyncAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is ProviderApiException or CalendarFlowException or HttpRequestException)
            {
                c.LastError = ex.Message;
                _log.LogInformation("İzin takvime yazılamadı ({Provider}): {Message}", c.Provider, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ toplantı

    private async Task<Dictionary<Guid, (string Name, string? Email)>> PeopleAsync(string tenant, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0) return new();
        return (await _sql.QueryAsync("""
            SELECT "Id", "FirstName" || ' ' || "LastName", "Email" FROM employee_employees WHERE "TenantSlug" = $1 AND "Id" = ANY($2)
            """, r => (Id: r.GetGuid(0), Name: r.GetString(1), Email: r.Str(2)), ct, tenant, list)).ToDictionary(x => x.Id, x => (x.Name, x.Email));
    }

    public async Task<Meeting> CreateMeetingAsync(GovernanceDbContext db, string tenant, Guid organizerId, MeetingRequest req, CancellationToken ct)
    {
        if (req.DurationMinutes is < 5 or > 600) throw new CalendarFlowException("Süre 5–600 dakika olmalı.");
        if (req.Provider is not ("zoom" or "teams" or "google" or "none")) throw new CalendarFlowException("Toplantı türü zoom, teams, google ya da none olmalı.");
        if (req.Provider != "none" && await TransferGuard.MissingAsync(db, tenant, TransferGuard.KeyOf(req.Provider), ct) is { } transferError)
            throw new CalendarFlowException(transferError);
        var participants = req.ParticipantEmployeeIds.Where(p => p != organizerId).Distinct().ToList();
        var people = await PeopleAsync(tenant, participants.Append(organizerId), ct);
        if (!people.ContainsKey(organizerId)) throw new CalendarFlowException("Düzenleyicinin çalışan kaydı bulunamadı.");
        var externals = req.ExternalEmails.Select(e => e.Trim()).Where(e => e.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var attendees = participants.Where(people.ContainsKey).Select(p => people[p]).Where(p => p.Email is not null)
            .Select(p => new Attendee(p.Email!, p.Name)).Concat(externals.Select(e => new Attendee(e, null))).ToList();

        var m = new Meeting
        {
            TenantSlug = tenant, SourceType = req.SourceType, SourceId = req.SourceId, Title = req.Title, Description = req.Description,
            StartsAt = DateTime.SpecifyKind(req.StartsAtUtc, DateTimeKind.Utc), DurationMinutes = req.DurationMinutes, Provider = req.Provider,
            OrganizerEmployeeId = organizerId, ParticipantEmployeeIds = participants, ExternalEmails = externals,
        };
        var end = m.StartsAt.AddMinutes(m.DurationMinutes);
        var warnings = new List<string>();
        var organizerConn = await db.CalendarConnections.FirstOrDefaultAsync(c => c.TenantSlug == tenant && c.EmployeeId == organizerId && c.Status == "Active"
            && (req.Provider == "teams" ? c.Provider == "Microsoft" : req.Provider == "google" ? c.Provider == "Google" : true), ct);

        if (req.Provider == "zoom")
        {
            var zcfg = await ConfigAsync(db, tenant, "Zoom", ct) ?? throw new CalendarFlowException("Zoom entegrasyonu yapılandırılmamış (Entegrasyonlar › Takvim ve toplantı).");
            var hostEmail = people[organizerId].Email;
            try { (m.ExternalMeetingId, m.JoinUrl) = await _zoom.CreateMeetingAsync(zcfg, hostEmail ?? zcfg.ZoomDefaultHost ?? "me", m.Title, m.Description, m.StartsAt, m.DurationMinutes, ct); }
            catch (ProviderApiException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound && !string.IsNullOrEmpty(zcfg.ZoomDefaultHost))
            {
                // Düzenleyicinin Zoom hesabı yok: varsayılan sunucu kullanıcısı adına açılır.
                (m.ExternalMeetingId, m.JoinUrl) = await _zoom.CreateMeetingAsync(zcfg, zcfg.ZoomDefaultHost!, m.Title, m.Description, m.StartsAt, m.DurationMinutes, ct);
                warnings.Add($"{hostEmail} Zoom hesabında bulunamadı; toplantı {zcfg.ZoomDefaultHost} adına açıldı.");
            }
            catch (ProviderApiException ex) { throw new CalendarFlowException(ex.Message); }
        }
        else if (req.Provider is "teams" or "google" && organizerConn is null)
            throw new CalendarFlowException(req.Provider == "teams"
                ? "Teams toplantısı için önce Microsoft 365 (Outlook) takviminizi bağlayın (Profil › Takvim)."
                : "Google Meet için önce Google Takvim'inizi bağlayın (Profil › Takvim).");

        db.Meetings.Add(m);
        var description = (m.Description is null ? "" : m.Description + "\n\n") + (m.JoinUrl is null ? "" : $"Katılım: {m.JoinUrl}\n") + "HR360 üzerinden planlandı.";

        if (organizerConn is not null && (req.AddToCalendars || req.Provider is "teams" or "google"))
        {
            // Düzenleyicinin takvimine katılımcılarla birlikte: davetler takvim sağlayıcısınca gönderilir.
            try
            {
                var token = await AccessTokenAsync(db, organizerConn, ct);
                var ev = await Provider(organizerConn.Provider).CreateEventAsync(token, new CalendarEventSpec(m.Title, description, m.StartsAt, end, null, null,
                    attendees, NativeOnlineMeeting: req.Provider is "teams" or "google", m.JoinUrl, false), ct);
                db.CalendarEventLinks.Add(new CalendarEventLink { TenantSlug = tenant, ConnectionId = organizerConn.Id, SourceType = "meeting", SourceId = m.Id, ExternalEventId = ev.Id });
                if (req.Provider is "teams" or "google")
                {
                    m.JoinUrl = ev.JoinUrl;
                    m.ExternalMeetingId = ev.Id;
                    if (ev.JoinUrl is null) warnings.Add("Takvim etkinliği oluştu ama toplantı bağlantısı dönmedi (hesabın çevrimiçi toplantı izni olmayabilir).");
                }
            }
            catch (Exception ex) when (ex is ProviderApiException or CalendarFlowException or HttpRequestException)
            {
                if (req.Provider is "teams" or "google") throw new CalendarFlowException(ex.Message);
                warnings.Add($"Takviminize eklenemedi: {ex.Message}");
            }
        }
        else if (req.AddToCalendars)
        {
            // Düzenleyici takvim bağlamamış: bağlamış her katılımcının kendi takvimine (davetsiz) eklenir.
            var conns = await db.CalendarConnections.Where(c => c.TenantSlug == tenant && c.Status == "Active" && participants.Contains(c.EmployeeId)).ToListAsync(ct);
            foreach (var c in conns.GroupBy(c => c.EmployeeId).Select(g => g.First()))
            {
                try
                {
                    var token = await AccessTokenAsync(db, c, ct);
                    var ev = await Provider(c.Provider).CreateEventAsync(token, new CalendarEventSpec(m.Title, description, m.StartsAt, end, null, null,
                        Array.Empty<Attendee>(), false, m.JoinUrl, false), ct);
                    db.CalendarEventLinks.Add(new CalendarEventLink { TenantSlug = tenant, ConnectionId = c.Id, SourceType = "meeting", SourceId = m.Id, ExternalEventId = ev.Id });
                }
                catch (Exception ex) when (ex is ProviderApiException or CalendarFlowException or HttpRequestException)
                {
                    warnings.Add($"{people.GetValueOrDefault(c.EmployeeId).Name}: takvime eklenemedi ({ex.Message})");
                }
            }
            if (conns.Count == 0 && participants.Count > 0) warnings.Add("Katılımcıların hiçbiri takvim bağlamamış; bağlantıyı kendileriyle paylaşın.");
        }
        m.Warnings = warnings.Count == 0 ? null : string.Join("\n", warnings);
        await db.SaveChangesAsync(ct);
        return m;
    }

    public async Task CancelMeetingAsync(GovernanceDbContext db, Meeting m, CancellationToken ct)
    {
        if (m.Provider == "zoom" && m.ExternalMeetingId is not null && await ConfigAsync(db, m.TenantSlug, "Zoom", ct) is { } zcfg)
        {
            try { await _zoom.DeleteMeetingAsync(zcfg, m.ExternalMeetingId, ct); }
            catch (ProviderApiException ex) { _log.LogInformation("Zoom toplantısı silinemedi: {Message}", ex.Message); }
        }
        var links = await db.CalendarEventLinks.Where(l => l.TenantSlug == m.TenantSlug && l.SourceType == "meeting" && l.SourceId == m.Id).ToListAsync(ct);
        foreach (var l in links)
        {
            var c = await db.CalendarConnections.FirstOrDefaultAsync(x => x.Id == l.ConnectionId, ct);
            if (c is null) continue;
            try { await Provider(c.Provider).DeleteEventAsync(await AccessTokenAsync(db, c, ct), l.ExternalEventId, ct); }
            catch (Exception ex) when (ex is ProviderApiException or CalendarFlowException or HttpRequestException) { _log.LogInformation("Takvim etkinliği silinemedi: {Message}", ex.Message); }
            db.CalendarEventLinks.Remove(l);
        }
        m.Status = "Cancelled";
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ boş / dolu

    public sealed record Busy(DateTime Start, DateTime End, string Source);
    public sealed record PersonAvailability(Guid EmployeeId, string Name, bool CalendarConnected, List<Busy> Busy);

    /// <summary>
    /// Kişilerin dolu aralıkları: bağlı takvim (yalnızca dolu/boş, başlık yok) + HR360'taki
    /// onaylı izinler ve planlı 1:1'ler. Takvim bağlamayanlar için yalnızca HR360 verisi.
    /// </summary>
    public async Task<List<PersonAvailability>> AvailabilityAsync(GovernanceDbContext db, string tenant, IReadOnlyList<Guid> ids, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var people = await PeopleAsync(tenant, ids, ct);
        var conns = await db.CalendarConnections.Where(c => c.TenantSlug == tenant && c.Status == "Active" && ids.Contains(c.EmployeeId)).ToListAsync(ct);
        var allowedTransfers = await TransferGuard.AllowedAsync(db, tenant, ct);
        conns = conns.Where(c => allowedTransfers.Contains(TransferGuard.KeyOf(c.Provider))).ToList();
        var leaves = await _sql.QueryAsync("""
            SELECT "EmployeeId", "StartDate", "EndDate" FROM leave_requests
            WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2) AND "Status" = 'Approved' AND "EndDate" >= $3 AND "StartDate" <= $4
            """, r => (Emp: r.GetGuid(0), S: r.GetFieldValue<DateOnly>(1), E: r.GetFieldValue<DateOnly>(2)), ct, tenant, ids.ToArray(),
            DateOnly.FromDateTime(fromUtc), DateOnly.FromDateTime(toUtc));
        var ones = await _sql.QueryAsync("""
            SELECT o."EmployeeId", e."Id", o."ScheduledAt" FROM engagement_one_on_ones o
            LEFT JOIN employee_employees e ON e."TenantSlug" = o."TenantSlug" AND e."KeycloakUserId" = o."ManagerUserId"
            WHERE o."TenantSlug" = $1 AND o."Status" = 'Planned' AND o."ScheduledAt" BETWEEN $2 AND $3
            """, r => (Emp: r.GetGuid(0), Mgr: r.GuidOrNull(1), At: r.GetFieldValue<DateTime>(2)), ct, tenant, fromUtc, toUtc);
        var result = new List<PersonAvailability>();
        foreach (var id in ids.Distinct())
        {
            if (!people.TryGetValue(id, out var p)) continue;
            var busy = new List<Busy>();
            foreach (var l in leaves.Where(l => l.Emp == id))
                busy.Add(new(l.S.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-3), l.E.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-3), "izin"));
            foreach (var o in ones.Where(o => o.Emp == id || o.Mgr == id))
                busy.Add(new(DateTime.SpecifyKind(o.At, DateTimeKind.Utc), DateTime.SpecifyKind(o.At, DateTimeKind.Utc).AddMinutes(30), "1:1"));
            var conn = conns.FirstOrDefault(c => c.EmployeeId == id);
            if (conn is not null)
            {
                try
                {
                    var token = await AccessTokenAsync(db, conn, ct);
                    busy.AddRange((await Provider(conn.Provider).BusyAsync(token, conn.AccountEmail, fromUtc, toUtc, ct)).Select(b => new Busy(b.Start, b.End, "takvim")));
                }
                catch (Exception ex) when (ex is ProviderApiException or CalendarFlowException or HttpRequestException) { conn.LastError = ex.Message; }
            }
            result.Add(new(id, p.Name, conn is not null, busy.OrderBy(b => b.Start).ToList()));
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Herkesin boş olduğu ilk aralıklar (iş saatleri 09:00–18:00 TR, hafta içi).</summary>
    public static List<DateTime> Suggest(List<PersonAvailability> people, DateTime fromUtc, DateTime toUtc, int minutes, int max = 5)
    {
        var all = people.SelectMany(p => p.Busy).ToList();
        var outList = new List<DateTime>();
        var t = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, fromUtc.Minute >= 30 ? 30 : 0, 0, DateTimeKind.Utc).AddMinutes(30);
        while (t.AddMinutes(minutes) <= toUtc && outList.Count < max)
        {
            var local = t.AddHours(3);
            var inHours = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && local.Hour >= 9 && local.AddMinutes(minutes) <= local.Date.AddHours(18);
            if (inHours && !all.Any(b => b.Start < t.AddMinutes(minutes) && b.End > t)) outList.Add(t);
            t = t.AddMinutes(30);
        }
        return outList;
    }
}
