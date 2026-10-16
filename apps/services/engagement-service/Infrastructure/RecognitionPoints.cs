using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Models;

namespace EngagementService.Infrastructure;

/// <summary>
/// Takdir puanı kuralları (yenilik haritası #25). Bakiye = hareket defteri toplamı; aylık bütçe =
/// kişinin bu takvim ayında (Türkiye saatiyle) takdirlerle verdiği puan. Aynı kişinin eşzamanlı iki
/// harcaması çift harcama yaratmasın diye işlem içinde kişi başına danışma kilidi alınır.
/// </summary>
public static class RecognitionPoints
{
    public static async Task<RecognitionSettings> SettingsAsync(EngagementDbContext db, CancellationToken ct) =>
        await db.RecognitionSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new RecognitionSettings();

    /// <summary>Türkiye saatine göre bu ayın başlangıcı (UTC).</summary>
    public static DateTime MonthStartUtc(DateTime nowUtc)
    {
        var tr = nowUtc.AddHours(3);
        return new DateTime(tr.Year, tr.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-3);
    }

    public static async Task<int> BalanceAsync(EngagementDbContext db, Guid employeeId, CancellationToken ct) =>
        await db.PointsLedger.Where(x => x.EmployeeId == employeeId).SumAsync(x => (int?)x.Delta, ct) ?? 0;

    public static async Task<int> GivenThisMonthAsync(EngagementDbContext db, Guid employeeId, CancellationToken ct)
    {
        var since = MonthStartUtc(DateTime.UtcNow);
        return await db.Kudos.Where(k => k.FromEmployeeId == employeeId && k.CreatedAt >= since).SumAsync(k => (int?)k.Points, ct) ?? 0;
    }

    /// <summary>Kişi başına işlem kilidi (işlem bitince kendiliğinden bırakılır).</summary>
    public static Task LockAsync(EngagementDbContext db, string tenant, Guid employeeId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", [$"points:{tenant}:{employeeId}"], ct);

    /// <summary>Puan verme kuralı (saf): açık olmalı, 1..MaxPerKudos, aylık bütçeyi aşmamalı.</summary>
    public static RuleError? ValidateGive(RecognitionSettings s, int points, int givenThisMonth, bool giverIsEmployee)
    {
        if (points == 0) return null;
        if (!s.Enabled) return RuleError.Bad("Takdir puanları kapalı.", "points_disabled");
        if (!giverIsEmployee) return RuleError.Bad("Puan vermek için çalışan kaydınız olmalı.", "points_no_employee");
        if (points < 0 || points > s.MaxPerKudos) return RuleError.Bad($"Bir takdirde en fazla {s.MaxPerKudos} puan verilebilir.", "points_max");
        if (givenThisMonth + points > s.MonthlyAllowance)
            return RuleError.Bad($"Bu ayki puan bütçenizde {Math.Max(0, s.MonthlyAllowance - givenThisMonth)} puan kaldı.", "points_allowance");
        return null;
    }
}
