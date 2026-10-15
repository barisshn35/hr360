using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;

namespace TimeShiftService.Services;

/// <summary>
/// Giriş-çıkış çekirdeği: web ucu (TimeEntriesController), QR/konum ve kart/PIN terminali
/// (TimeClockController) aynı kuralları kullanır.
/// </summary>
public static class ClockCore
{
    public const int StandardWorkMinutes = 480; // 8 saat

    /// <summary>
    /// Bir vardiyanin azami suresi. Daha eski acik kayit "unutulmus cikis" sayilir:
    /// calisan onu kapatamaz (23 saatlik sahte mesai olusmasin), yeniden giris
    /// yapabilir; eski kaydi yonetici/IK duzeltir.
    /// </summary>
    public const int MaxShiftHours = 16;

    public const string ErrOpenEntry = "Açık bir giriş kaydınız var; önce çıkış yapın";
    public const string ErrAlreadyIn = "Bu gün için giriş kaydı zaten var";
    public const string ErrNoOpenEntry = "Önce giriş kaydı oluşturulmalı";

    /// <summary>İş günü işletmenin saat dilimine göre belirlenir (HR360_TIMEZONE, varsayılan Europe/Istanbul).</summary>
    public static readonly TimeZoneInfo BusinessZone = ResolveZone();
    private static TimeZoneInfo ResolveZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("HR360_TIMEZONE") ?? "Europe/Istanbul"); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }
    public static DateOnly WorkDate(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, BusinessZone).DateTime);

    public static Task<TimeEntry?> OpenEntryAsync(TimeShiftDbContext db, Guid employeeId, DateTimeOffset at, CancellationToken ct) =>
        db.TimeEntries
            .Where(t => t.EmployeeId == employeeId && t.ClockIn != null && t.ClockOut == null
                && t.ClockIn > at.AddHours(-MaxShiftHours) && t.ClockIn <= at)
            .OrderByDescending(t => t.ClockIn)
            .FirstOrDefaultAsync(ct);

    /// <summary>Giriş. Hata iletisi ya da kayıt.</summary>
    public static async Task<(string? Error, TimeEntry? Entry)> ClockInAsync(TimeShiftDbContext db, Guid employeeId, DateTimeOffset at,
        TimeEntrySource source, CancellationToken ct)
    {
        if (await OpenEntryAsync(db, employeeId, at, ct) is not null)
            return (ErrOpenEntry, null);
        var date = WorkDate(at);
        // Madde 72: kapatılmış puantaj dönemine kayıt girilemez.
        if (await PeriodLock.CheckAsync(db, date, ct) is { } locked) return (locked, null);
        var entry = await db.TimeEntries.FirstOrDefaultAsync(t => t.EmployeeId == employeeId && t.Date == date, ct);
        if (entry is not null && entry.ClockIn is not null)
            return (ErrAlreadyIn, null);
        entry ??= new TimeEntry { EmployeeId = employeeId, Date = date };
        entry.ClockIn = at;
        entry.Source = source;
        if (db.Entry(entry).State == EntityState.Detached) db.TimeEntries.Add(entry);
        return (null, entry);
    }

    /// <summary>Çıkış: son MaxShiftHours içindeki açık kayıt kapatılır (gece vardiyası dahil).</summary>
    public static async Task<(string? Error, TimeEntry? Entry)> ClockOutAsync(TimeShiftDbContext db, Guid employeeId, DateTimeOffset at, CancellationToken ct)
    {
        var entry = await OpenEntryAsync(db, employeeId, at, ct);
        if (entry?.ClockIn is null) return (ErrNoOpenEntry, null);
        if (await PeriodLock.CheckAsync(db, entry.Date, ct) is { } locked) return (locked, null);
        entry.ClockOut = at;
        var worked = (int)(at - entry.ClockIn.Value).TotalMinutes;
        entry.WorkedMinutes = worked;
        entry.OvertimeMinutes = Math.Max(0, worked - StandardWorkMinutes);
        return (null, entry);
    }

    /// <summary>
    /// Giriş-çıkış hareketi: kind "in" | "out" | "auto" (açık kayıt varsa çıkış). Kayıt ve hareket
    /// tek SaveChanges ile yazılır. Web/QR, kart/PIN terminali ve sohbet (iç uç) bu yolu kullanır.
    /// </summary>
    public static async Task<(string? Error, TimeClockPunch? Punch, TimeEntry? Entry)> PunchAsync(TimeShiftDbContext db, Guid employeeId,
        Guid? siteId, string kind, TimeEntrySource method, bool? onSite, CancellationToken ct, Action<TimeClockPunch>? decorate = null)
    {
        var at = DateTimeOffset.UtcNow;
        var open = await OpenEntryAsync(db, employeeId, at, ct);
        var goingIn = kind switch { "in" => true, "out" => false, _ => open is null };
        var (err, entry) = goingIn
            ? await ClockInAsync(db, employeeId, at, method, ct)
            : await ClockOutAsync(db, employeeId, at, ct);
        if (err is not null) return (err, null, null);
        var p = new TimeClockPunch { EmployeeId = employeeId, SiteId = siteId, Kind = goingIn ? PunchKind.In : PunchKind.Out, Method = method, OnSite = onSite, At = at };
        decorate?.Invoke(p);
        db.TimeClockPunches.Add(p);
        await db.SaveChangesAsync(ct);
        return (null, p, entry);
    }

    private static readonly (int Max, string Label)[] Buckets =
        { (50, "0-50"), (100, "50-100"), (250, "100-250"), (500, "250-500"), (1000, "500-1000") };

    /// <summary>Madde 67: uzaklığın kaba aralığı (metre). Konum yerine yalnızca bu aralık saklanır.</summary>
    public static string DistanceBucket(double meters)
    {
        foreach (var (max, label) in Buckets)
            if (meters < max) return label;
        return "1000+";
    }

    /// <summary>Saklama süresi dolan ham koordinatları siler (yalnızca şirket ham saklamayı açtıysa oluşur).</summary>
    public static async Task PurgeExpiredRawAsync(TimeShiftDbContext db, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await db.TimeClockPunches.Where(p => p.RawExpiresAt != null && p.RawExpiresAt < now)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.RawLatitude, (double?)null).SetProperty(p => p.RawLongitude, (double?)null)
                    .SetProperty(p => p.RawExpiresAt, (DateTimeOffset?)null), ct);
        }
        catch (InvalidOperationException) { /* bellek içi test veritabanı toplu güncellemeyi desteklemez */ }
    }

    /// <summary>İki nokta arası uzaklık (metre, haversine).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
