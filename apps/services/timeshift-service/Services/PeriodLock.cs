using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;

namespace TimeShiftService.Services;

/// <summary>
/// Madde 72: puantaj dönemi kilidi. Kapalı ayın (Status = "Closed") giriş-çıkışı, puantaj düzeltmesi ve
/// fazla mesai talebi/kararı reddedilir; bordro o ayın onaylı fazla mesaisini değişmeyecek biçimde okur.
/// </summary>
public static class PeriodLock
{
    public const string Closed = "Closed";
    public const string Open = "Open";

    public static string Message(DateOnly date) =>
        $"{date:MM.yyyy} puantaj dönemi kapatılmış; bu tarih için kayıt değiştirilemez (İK dönemi yeniden açabilir)";

    public static Task<bool> IsLockedAsync(TimeShiftDbContext db, DateOnly date, CancellationToken ct) =>
        db.TimesheetPeriods.AsNoTracking().AnyAsync(p => p.Year == date.Year && p.Month == date.Month && p.Status == Closed, ct);

    /// <summary>Kapalıysa Türkçe hata iletisi, açıksa null.</summary>
    public static async Task<string?> CheckAsync(TimeShiftDbContext db, DateOnly date, CancellationToken ct) =>
        await IsLockedAsync(db, date, ct) ? Message(date) : null;

    /// <summary>Ay geçerli mi (dönem kapatma girdisi): 2000–2100, 1–12, gelecek ay kapatılamaz.</summary>
    public static string? ValidateMonth(int year, int month, DateOnly today)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12) return "Geçersiz dönem";
        if (new DateOnly(year, month, 1) > new DateOnly(today.Year, today.Month, 1)) return "Gelecek bir dönem kapatılamaz";
        return null;
    }
}
