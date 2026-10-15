using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompensationService.Models;

namespace CompensationService.Payroll;

/* ======================================================================
 * Bordro dalgası 8 — saf hesaplar (veritabanına dokunmaz; birim testleri
 * tests/dotnet/Compensation.Tests/PayrollTrTests.cs).
 * ==================================================================== */

/// <summary>Fark bordrosu adayı: kapanmış dönemin eski ve yeni (geriye dönük ücretle) brütü.</summary>
public sealed record RetroCandidate(Guid EmployeeId, Guid SourcePeriodId, int Year, int Month, decimal OldBase, decimal NewBase,
    decimal OldGross, decimal NewGross, decimal AlreadyPaid, decimal DiffGross, decimal OldNet, decimal NewNet)
{
    public string Label => $"Fark: {Year}/{Month:00}";
}

/// <summary>
/// Madde 63 — fark bordrosu. Kapanmış dönemin pusulası saklı girdileriyle (ödenen gün, fazla mesai saati,
/// ek ödeme/kesinti, önceki kümülatif matrah) yeni aylık brütle YENİDEN hesaplanır; brüt farkından daha
/// önce ödenmiş farklar düşülür. Kapanmış dönem değişmez; fark açık bir dönemde ek ödeme olur.
/// </summary>
public static class RetroPay
{
    public static RetroCandidate? Compute(PayrollParams p, Payslip old, decimal newBase, decimal alreadyPaid)
    {
        if (newBase <= 0) return null;
        var input = new PayrollInput(old.Month, newBase, old.UnpaidDays, old.OvertimeHours, old.Additions, old.Deductions,
            old.CumulativeTaxBase - old.TaxBase);
        var r = PayrollCalculator.Calculate(p, input);
        var diff = r.Gross - old.Gross - alreadyPaid;
        if (Math.Abs(diff) < 0.01m) return null;
        return new(old.EmployeeId, old.PeriodId, old.Year, old.Month, old.MonthlyBaseGross, newBase, old.Gross, r.Gross, alreadyPaid,
            diff, old.Net, r.Net);
    }

    /// <summary>Dönem sonunda geçerli ücret kaydı (hesaplamayla aynı kural: dönemle kesişen en son başlangıçlı kayıt).</summary>
    public static CompensationRecord? RecordFor(IEnumerable<CompensationRecord> employeeRecords, int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        return employeeRecords.Where(r => r.EffectiveFrom <= end && (r.EffectiveTo == null || r.EffectiveTo >= start))
            .OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
    }
}

/// <summary>Kıdem/ihbar girdisi.</summary>
public sealed record SeveranceInput(
    DateOnly HireDate, DateOnly LastWorkingDay, string Reason, decimal MonthlyBaseGross,
    decimal RegularAdditionsMonthly,     // giydirme: düzenli ek ödemelerin aylık ortalaması (son 12 kapanmış pusula)
    decimal OtherBenefitsMonthly,        // elle: yemek/yol gibi sürekli nakdî/aynî yardımların aylık brüt karşılığı
    decimal SeveranceCeiling,
    bool? SeveranceEligibleOverride,     // İK kararı (null = ayrılış nedenine göre)
    bool? NoticePayOverride,             // İK kararı (null = işveren feshinde öde)
    decimal UnusedLeaveDays,
    decimal PriorCumulativeTaxBase,      // ihbar GV'si için aynı yılın kümülatif matrahı
    IReadOnlyList<TaxBracket> Brackets,
    decimal StampTaxRate);

public sealed record SeveranceResult(
    int TenureDays, decimal TenureYears, decimal DressedMonthlyGross, decimal SeveranceBasis, bool CeilingApplied,
    bool SeveranceEligible, decimal SeveranceGross, decimal SeveranceStampTax, decimal SeveranceNet,
    int NoticeWeeks, bool NoticePaid, decimal NoticeGross, decimal NoticeIncomeTax, decimal NoticeStampTax, decimal NoticeNet,
    decimal UnusedLeaveDays, decimal UnusedLeaveGross, decimal TotalNet);

/// <summary>
/// Madde 61 — kıdem ve ihbar tazminatı (4857 s. İş K. m.17, 1475 s. K. m.14). Giydirilmiş brüt = aylık brüt
/// + düzenli ek ödemeler + sürekli yan hakların aylık karşılığı. Kıdem: giydirilmiş brüt (kıdem tavanıyla
/// sınırlı) × hizmet süresi (gün/365); gelir vergisinden istisna (GVK m.25/7), yalnızca damga vergisi.
/// İhbar: kıdeme göre 2/4/6/8 hafta × günlük giydirilmiş brüt; SGK primine tabi değil, gelir vergisi
/// (yılın kümülatif matrahıyla) ve damga vergisine tabi. Kullanılmayan izin ücreti yalnızca brüt gösterilir
/// (son bordroda ek ödeme olarak işlenir; SGK/GV/damga orada hesaplanır). Sonuç bir öneridir; İK onaylar.
/// </summary>
public static class SeveranceCalculator
{
    static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// İhbar süresi (m.17): "altı aydan az" 2, "altı aydan bir buçuk yıla kadar" 4, "bir buçuk yıldan üç yıla
    /// kadar" 6, "üç yıldan fazla" 8 hafta. "Kadar" üst sınırı içerir: tam 6 ay 4, tam 1,5 yıl 4, tam 3 yıl 6 hafta.
    /// Gün sayısıyla yaklaşık hesap (365 günlük yıl); takvime göre kesin hesap için tarihli aşırı yükleme.
    /// </summary>
    public static int NoticeWeeks(int tenureDays)
    {
        var years = tenureDays / 365m;
        return years < 0.5m ? 2 : years <= 1.5m ? 4 : years <= 3 ? 6 : 8;
    }

    /// <summary>
    /// İhbar süresi takvime göre: hizmet, işe giriş gününden son iş gününün ertesine kadar sürer
    /// (ör. 01.01.2023 – 31.12.2025 tam 3 yıl = 6 hafta; 01.01.2026'ya taşarsa 3 yıldan fazla = 8 hafta).
    /// </summary>
    public static int NoticeWeeks(DateOnly hireDate, DateOnly lastWorkingDay)
    {
        var end = lastWorkingDay.AddDays(1);
        if (end < hireDate.AddMonths(6)) return 2;
        if (end <= hireDate.AddMonths(18)) return 4;
        if (end <= hireDate.AddYears(3)) return 6;
        return 8;
    }

    /// <summary>Yıllık izin bakiyesi satırı (leave_balances, Type = Annual).</summary>
    public sealed record AnnualLeaveRow(int Year, decimal EntitledDays, decimal UsedDays, decimal CarriedOutDays);

    /// <summary>
    /// Kullanılmayan yıllık izin (İş K. m.59): ayrılış yılına kadar (dahil) tüm yılların kalanları. Önceki yıldan
    /// devreden gün sonraki yılın hakkına eklendiğinden devredilen kısım (CarriedOutDays) iki kez sayılmaz;
    /// devredilmemiş eski yıl kalanları da dahildir (yıllık izin hakkı zamanaşımına uğramadan ödenir).
    /// Sonraki yılların satırları sayılmaz. Toplam negatifse 0.
    /// </summary>
    public static decimal UnusedAnnualLeave(IEnumerable<AnnualLeaveRow> rows, int exitYear) =>
        Math.Max(0, rows.Where(r => r.Year <= exitYear).Sum(r => r.EntitledDays - r.UsedDays - r.CarriedOutDays));

    /// <summary>Kümülatif GV matrahına giren pusula (ihbar GV'si için).</summary>
    public sealed record TaxBaseSlip(int Year, int Month, decimal TaxBase, bool PeriodClosed);

    /// <summary>
    /// İhbar GV'si için ayrılış yılının kümülatif matrahı: devreden (HR360 dışı) matrah + ayrılış yılının
    /// YALNIZCA kapanmış dönemlerindeki, ayrılış ayına kadar (dahil) pusulaların matrahı. Açık/hesaplanmış
    /// (kesinleşmemiş) dönemler ve son iş gününden sonraki aylar sayılmaz.
    /// </summary>
    public static decimal PriorTaxBase(IEnumerable<TaxBaseSlip> slips, DateOnly lastWorkingDay, decimal openingTaxBase) =>
        Math.Max(0, openingTaxBase) + slips.Where(s => s.PeriodClosed && s.Year == lastWorkingDay.Year && s.Month <= lastWorkingDay.Month)
            .Sum(s => s.TaxBase);

    /// <summary>Varsayılan kıdem hakkı: en az 1 yıl ve işveren feshi / emeklilik / belirli süreli sözleşme sonu (İK değiştirebilir).</summary>
    public static bool DefaultEligible(int tenureDays, string reason) =>
        tenureDays >= 365 && reason is "Termination" or "Retirement" or "ContractEnd";

    public static SeveranceResult Compute(SeveranceInput i)
    {
        if (i.LastWorkingDay < i.HireDate) throw new ArgumentException("Son iş günü işe girişten önce olamaz");
        var days = i.LastWorkingDay.DayNumber - i.HireDate.DayNumber + 1;
        var years = days / 365m;
        var dressed = R(Math.Max(0, i.MonthlyBaseGross) + Math.Max(0, i.RegularAdditionsMonthly) + Math.Max(0, i.OtherBenefitsMonthly));
        var ceilingApplied = i.SeveranceCeiling > 0 && dressed > i.SeveranceCeiling;
        var basis = ceilingApplied ? i.SeveranceCeiling : dressed;
        var eligible = i.SeveranceEligibleOverride ?? DefaultEligible(days, i.Reason);
        var sevGross = eligible ? R(basis * days / 365m) : 0;
        var sevStamp = R(sevGross * i.StampTaxRate);

        var weeks = NoticeWeeks(i.HireDate, i.LastWorkingDay);
        var noticePaid = i.NoticePayOverride ?? i.Reason == "Termination";
        var noticeGross = noticePaid ? R(dressed / 30m * weeks * 7) : 0;
        var noticeTax = noticePaid ? PayrollCalculator.MonthlyTax(Math.Max(0, i.PriorCumulativeTaxBase), noticeGross, i.Brackets) : 0;
        var noticeStamp = R(noticeGross * i.StampTaxRate);

        var leaveDays = Math.Max(0, i.UnusedLeaveDays);
        var leaveGross = R(Math.Max(0, i.MonthlyBaseGross) / 30m * leaveDays);
        var sevNet = sevGross - sevStamp;
        var noticeNet = noticeGross - noticeTax - noticeStamp;
        return new(days, Math.Round(years, 4), dressed, basis, ceilingApplied, eligible, sevGross, sevStamp, sevNet,
            weeks, noticePaid, noticeGross, noticeTax, noticeStamp, noticeNet, leaveDays, leaveGross, sevNet + noticeNet);
    }

    /// <summary>İbraname şablonu yer tutucuları ({{kidem.brut}} gibi) için değerler (tr-TR biçimli).</summary>
    public static Dictionary<string, string> Variables(SeveranceResult r, DateOnly lastWorkingDay, string reasonTr)
    {
        var tr = new CultureInfo("tr-TR");
        string M(decimal v) => v.ToString("#,##0.00", tr) + " TL";
        return new()
        {
            ["ayrilis.tarih"] = lastWorkingDay.ToString("dd.MM.yyyy", tr), ["ayrilis.neden"] = reasonTr,
            ["kidem.gun"] = r.TenureDays.ToString(tr), ["kidem.yil"] = r.TenureYears.ToString("0.##", tr),
            ["ucret.giydirilmis"] = M(r.DressedMonthlyGross), ["kidem.esas"] = M(r.SeveranceBasis),
            ["kidem.brut"] = M(r.SeveranceGross), ["kidem.damga"] = M(r.SeveranceStampTax), ["kidem.net"] = M(r.SeveranceNet),
            ["ihbar.hafta"] = r.NoticeWeeks.ToString(tr), ["ihbar.brut"] = M(r.NoticeGross), ["ihbar.gv"] = M(r.NoticeIncomeTax),
            ["ihbar.damga"] = M(r.NoticeStampTax), ["ihbar.net"] = M(r.NoticeNet),
            ["izin.gun"] = r.UnusedLeaveDays.ToString("0.##", tr), ["izin.brut"] = M(r.UnusedLeaveGross),
            ["toplam.net"] = M(r.TotalNet),
        };
    }

    public static string ReasonTr(string reason) => reason switch
    {
        "Resignation" => "İstifa", "Termination" => "İşveren feshi", "Retirement" => "Emeklilik",
        "ContractEnd" => "Belirli süreli sözleşmenin sona ermesi", _ => "Diğer",
    };

    /// <summary>
    /// Şirkette "İbraname" şablonu yoksa kullanılan varsayılan metin (governance belge şablonu yer tutucularıyla).
    /// Hukuki metin değildir; şirket hukuk birimince uyarlanmalıdır (ibraname geçerlilik şartları: TBK m.420).
    /// </summary>
    public const string DefaultReleaseTemplate = """
        <h2 style="text-align:center">İBRANAME</h2>
        <p><b>{{sirket.ad}}</b> nezdinde <b>{{calisan.iseGiris}}</b> – <b>{{ayrilis.tarih}}</b> tarihleri arasında
        <b>{{calisan.pozisyon}}</b> olarak çalıştım. İş sözleşmem <b>{{ayrilis.neden}}</b> nedeniyle sona ermiştir.</p>
        <table style="width:100%;border-collapse:collapse" border="1" cellpadding="4">
        <tr><td>Hizmet süresi</td><td>{{kidem.gun}} gün ({{kidem.yil}} yıl)</td></tr>
        <tr><td>Giydirilmiş aylık brüt ücret</td><td>{{ucret.giydirilmis}}</td></tr>
        <tr><td>Kıdem tazminatı (brüt / damga / net)</td><td>{{kidem.brut}} / {{kidem.damga}} / {{kidem.net}}</td></tr>
        <tr><td>İhbar tazminatı ({{ihbar.hafta}} hafta; brüt / GV / damga / net)</td><td>{{ihbar.brut}} / {{ihbar.gv}} / {{ihbar.damga}} / {{ihbar.net}}</td></tr>
        <tr><td>Kullanılmayan yıllık izin ({{izin.gun}} gün, brüt)</td><td>{{izin.brut}}</td></tr>
        <tr><td><b>Toplam net (kıdem + ihbar)</b></td><td><b>{{toplam.net}}</b></td></tr>
        </table>
        <p>Yukarıdaki tutarların banka aracılığıyla tarafıma ödendiğini beyan ederim. Bu belge, ödemenin hesap
        dökümünü gösterir; ödeme tarihinden en az bir ay sonra düzenlenmesi ve ödemenin banka aracılığıyla
        yapılması gerekir (TBK m.420).</p>
        <p style="margin-top:48px">{{bugun}}</p><p style="text-align:right">{{calisan.adSoyad}}<br/>İmza</p>
        """;
}

/// <summary>
/// Madde 59 — e-bordro içerik özeti. Pusulanın kanonik JSON'u (sabit alan sırası, tutarlar "0.00") ve SHA-256
/// özeti; çalışan "Okudum, teslim aldım" dediğinde bu özet kayda geçer. Dönem yeniden açılıp pusula değişirse
/// özet tutmaz ("değişti" uyarısı). Teslim edilen içerik ayrıca şifreli (AES-256-GCM) saklanır.
/// </summary>
public static class EPayslip
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Canonical(Payslip s)
    {
        static string M(decimal v) => v.ToString("0.00", Inv);
        var fields = new (string, string)[]
        {
            ("id", s.Id.ToString()), ("employeeId", s.EmployeeId.ToString()), ("year", s.Year.ToString(Inv)), ("month", s.Month.ToString(Inv)),
            ("currency", s.Currency), ("monthlyBaseGross", M(s.MonthlyBaseGross)), ("paidDays", s.PaidDays.ToString(Inv)),
            ("unpaidDays", s.UnpaidDays.ToString(Inv)), ("overtimeHours", M(s.OvertimeHours)), ("baseGross", M(s.BaseGross)),
            ("overtimePay", M(s.OvertimePay)), ("additions", M(s.Additions)), ("gross", M(s.Gross)), ("sgkBase", M(s.SgkBase)),
            ("sgkEmployee", M(s.SgkEmployee)), ("unemploymentEmployee", M(s.UnemploymentEmployee)), ("taxBase", M(s.TaxBase)),
            ("cumulativeTaxBase", M(s.CumulativeTaxBase)), ("incomeTax", M(s.IncomeTax)), ("incomeTaxExemption", M(s.IncomeTaxExemption)),
            ("stampTax", M(s.StampTax)), ("stampTaxExemption", M(s.StampTaxExemption)), ("deductions", M(s.Deductions)), ("net", M(s.Net)),
        };
        var sb = new StringBuilder("{");
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(fields[i].Item1)).Append(':').Append(JsonSerializer.Serialize(fields[i].Item2));
        }
        return sb.Append('}').ToString();
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Hash(Payslip s) => Sha256(Canonical(s));

    public static string Sha256Bytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>IP adresinin /24 (IPv4) ya da /48 (IPv6) öneki (onay kaydında tam IP tutulmaz).</summary>
    public static string? IpPrefix(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !System.Net.IPAddress.TryParse(ip.Trim(), out var a)) return null;
        var b = a.GetAddressBytes();
        if (b.Length == 4) return $"{b[0]}.{b[1]}.{b[2]}.0/24";
        return $"{b[0]:x2}{b[1]:x2}:{b[2]:x2}{b[3]:x2}:{b[4]:x2}{b[5]:x2}::/48";
    }
}
