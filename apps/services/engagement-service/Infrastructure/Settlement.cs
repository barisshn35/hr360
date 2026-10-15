namespace EngagementService.Infrastructure;

/// <summary>
/// İşten ayrılışta tahmini hak ediş (4857 s. İş Kanunu). Saf hesap: veritabanına
/// dokunmaz, birim testleri tests/dotnet altında.
/// </summary>
public static class SettlementCalculator
{
    /// <summary>
    /// SeveranceCeilingMissing: kıdem tavanı/damga oranı bordro parametrelerinde girilmemiş — kıdem tutarı
    /// hesaplanmaz (0; yanıtta null), toplam brüte girmez. Değer varsayılmaz (ürün kararı: parametreleri kullanıcı girer).
    /// </summary>
    public sealed record Result(
        decimal TenureYears, int NoticeWeeks, bool SeveranceEligible, decimal Severance, decimal SeveranceStampTax, decimal SeveranceNet,
        bool NoticeApplies, decimal Notice, decimal LeavePay, decimal TotalGross, bool SeveranceCeilingMissing = false);

    /// <summary>İhbar süresi (m.17): 6 aydan az 2, 6 ay–1,5 yıl 4, 1,5–3 yıl 6, 3 yıldan fazla 8 hafta.</summary>
    public static int NoticeWeeks(decimal years) => years < 0.5m ? 2 : years < 1.5m ? 4 : years < 3 ? 6 : 8;

    /// <summary>Kıdem tazminatı hakkı: en az 1 yıl ve işveren feshi / emeklilik / belirli süreli sözleşme sonu.</summary>
    public static bool SeveranceEligible(decimal years, string reason) =>
        years >= 1 && reason is "Termination" or "Retirement" or "ContractEnd";

    /// <param name="severanceCeiling">Kıdem tavanı (bordro parametreleri); null = girilmemiş.</param>
    /// <param name="stampTaxRate">Damga vergisi oranı (bordro parametreleri); null = girilmemiş.</param>
    public static Result Compute(DateOnly hireDate, DateOnly lastWorkingDay, string reason, decimal? grossMonthly, decimal remainingLeaveDays,
        decimal? severanceCeiling, decimal? stampTaxRate)
    {
        var days = Math.Max(0, lastWorkingDay.DayNumber - hireDate.DayNumber);
        var years = days / 365.25m;
        var noticeWeeks = NoticeWeeks(years);
        var eligible = SeveranceEligible(years, reason);
        var missing = severanceCeiling is not > 0 || stampTaxRate is null;
        var basis = grossMonthly is null || missing ? 0 : Math.Min(grossMonthly.Value, severanceCeiling!.Value);
        var severance = eligible && !missing ? Math.Round(basis * years, 2) : 0;
        var stamp = stampTaxRate ?? 0;
        var daily = grossMonthly is null ? 0 : grossMonthly.Value / 30m;
        var noticeApplies = reason == "Termination";
        var notice = noticeApplies ? Math.Round(daily * noticeWeeks * 7, 2) : 0;
        var leavePay = Math.Round(daily * Math.Max(0, remainingLeaveDays), 2);
        return new(Math.Round(years, 2), noticeWeeks, eligible, severance, Math.Round(severance * stamp, 2), Math.Round(severance * (1 - stamp), 2),
            noticeApplies, notice, leavePay, severance + notice + leavePay, missing);
    }
}
