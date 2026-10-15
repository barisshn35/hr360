using System.Text.Json;
using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>
/// Bordro parametre girdisi (PUT payroll/parameters/{yıl} ve önizleme). Tüm alanlar boş gelebilir; zorunlu
/// olanlar <see cref="PayrollParameterResolver.ValidateInput"/> ile denetlenir (sessiz varsayılan yok).
/// Oranlar 0–1 arası kesir (0,14 = %14); teşvik puan (2 = 2 puan).
/// </summary>
public sealed record PayrollParameterInput(
    int? ValidFromMonth = null,
    decimal? MinimumWageGross = null,
    decimal? MinimumWageNet = null,
    decimal? SgkEmployeeRate = null,
    decimal? UnemploymentEmployeeRate = null,
    decimal? SgkEmployerRate = null,
    decimal? EmployerIncentivePoints = null,
    decimal? UnemploymentEmployerRate = null,
    decimal? StampTaxRate = null,
    decimal? SgkCeilingMultiplier = null,
    List<TaxBracket>? Brackets = null,
    bool? MinimumWageExemption = null,
    decimal? AgiMonthly = null,
    decimal? SeveranceCeilingH1 = null,
    decimal? SeveranceCeilingH2 = null,
    /// <summary>Eski istemciler için kabul edilir, YOK SAYILIR: kullanıcının kaydettiği satır her zaman "girildi" sayılır.</summary>
    bool? Verified = null,
    string? Source = null);

/// <summary>Alan düzeyinde doğrulama hatası (Field: istemcideki camelCase alan adı).</summary>
public sealed record ParameterError(string Field, string Message);

/// <summary>
/// Madde 60 — bordro parametrelerinin dönem tarihine göre seçimi (saf; birim testli). Bir yılda birden
/// fazla satır olabilir (ValidFromMonth); dönem ayına göre yürürlüğe en son giren satır kullanılır.
///
/// Ürün kararı: yıllık parametreleri bordro yetkilisi GİRER. O yıl/ay için satır yoksa koddaki bir değere
/// düşülmez; <see cref="Resolve"/> null döner ve çağıran 409 <c>payroll_parameters_missing</c> verir.
/// </summary>
public static class PayrollParameterResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string MissingCode = "payroll_parameters_missing";

    /// <summary>Ay için geçerli satır (yoksa null).</summary>
    public static PayrollParameterSet? RowFor(IEnumerable<PayrollParameterSet> rows, int year, int month) =>
        rows.Where(r => r.Year == year && r.ValidFromMonth <= Math.Clamp(month, 1, 12))
            .OrderByDescending(r => r.ValidFromMonth).FirstOrDefault();

    /// <summary>Dönemin parametreleri; satır yoksa (ya da dilimleri okunamıyorsa) null — hesaplama yapılmaz.</summary>
    public static PayrollParams? Resolve(IEnumerable<PayrollParameterSet> rows, int year, int month)
    {
        var row = RowFor(rows, year, month);
        return row is null ? null : FromRow(row);
    }

    public static List<TaxBracket>? ParseBrackets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<TaxBracket>>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Satırdan hesap parametreleri. Dilimler boş/bozuksa null (eksik sayılır).</summary>
    public static PayrollParams? FromRow(PayrollParameterSet row)
    {
        var brackets = ParseBrackets(row.BracketsJson);
        if (brackets is not { Count: > 0 } || row.MinimumWageGross <= 0) return null;
        return new PayrollParams(row.Year, row.MinimumWageGross, row.SgkEmployeeRate, row.UnemploymentEmployeeRate, row.SgkEmployerRate,
            row.EmployerIncentivePoints, row.UnemploymentEmployerRate, row.StampTaxRate, row.SgkCeilingMultiplier,
            PayrollStatutory.OvertimeMultiplier, PayrollStatutory.MonthlyHours, brackets, row.MinimumWageExemption);
    }

    /// <summary>Doğrulanmış girdiden hesap parametreleri (önizleme; kaydetmeden).</summary>
    public static PayrollParams FromInput(int year, PayrollParameterInput i) => new(year, i.MinimumWageGross!.Value, i.SgkEmployeeRate!.Value,
        i.UnemploymentEmployeeRate!.Value, i.SgkEmployerRate!.Value, i.EmployerIncentivePoints!.Value, i.UnemploymentEmployerRate!.Value,
        i.StampTaxRate!.Value, i.SgkCeilingMultiplier!.Value, PayrollStatutory.OvertimeMultiplier, PayrollStatutory.MonthlyHours,
        i.Brackets!, i.MinimumWageExemption ?? true);

    /// <summary>
    /// Kıdem tazminatı tavanı (tarihin yarıyılına göre: Ocak–Haziran H1, Temmuz–Aralık H2). O tarihte yürürlükteki
    /// satırlardan (en yeniden eskiye) ilgili yarıyıl değeri girilmiş ilki; hiçbirinde yoksa null — tavan
    /// varsayılmaz, kıdem hesabı yapılmaz. H2 boşsa H1'e düşülmez (tavan Temmuz'da değişir).
    /// </summary>
    public static decimal? SeveranceCeilingOrNull(IEnumerable<PayrollParameterSet> rows, DateOnly date) =>
        rows.Where(r => r.Year == date.Year && r.ValidFromMonth <= date.Month).OrderByDescending(r => r.ValidFromMonth)
            .Select(r => date.Month <= 6 ? r.SeveranceCeilingH1 : r.SeveranceCeilingH2)
            .FirstOrDefault(v => v is > 0);

    /// <summary>409 gövdesi: dönem için parametre satırı yok.</summary>
    public static object Missing(int year, int month) => new
    {
        code = MissingCode, year, month, missing = "parameters",
        message = MissingMessage(year, month),
    };

    public static string MissingMessage(int year, int month) =>
        $"{year} yılı {month}. ay için bordro parametreleri girilmemiş. Bordro › Parametreler sekmesinde bu dönemin değerlerini resmî kaynaklarla karşılaştırarak girin.";

    /// <summary>409 gövdesi: kıdem tavanı girilmemiş.</summary>
    public static object SeveranceCeilingMissing(DateOnly date) => new
    {
        code = MissingCode, year = date.Year, month = date.Month, missing = "severanceCeiling",
        message = $"{date.Year} yılı {(date.Month <= 6 ? "Ocak–Haziran" : "Temmuz–Aralık")} kıdem tazminatı tavanı girilmemiş. Bordro › Parametreler sekmesinde kıdem tavanını girin.",
    };

    /// <summary>
    /// Girdi denetimi (alan bazında). Boş liste = geçerli. Zorunlu: asgari ücret (brüt), SGK işçi/işveren,
    /// işsizlik işçi/işveren, teşvik puanı, damga, SGK tavan çarpanı, dilimler.
    /// </summary>
    public static List<ParameterError> ValidateInput(int year, PayrollParameterInput i)
    {
        var e = new List<ParameterError>();
        void Add(string f, string m) => e.Add(new ParameterError(f, m));
        void Rate(string f, decimal? v, decimal max, string label)
        {
            if (v is null) Add(f, "Zorunlu alan");
            else if (v < 0 || v > max) Add(f, $"{label} %0 ile %{max * 100:0.##} arasında olmalı");
        }

        if (year is < 2020 or > 2100) Add("year", "Geçersiz yıl");
        if (i.ValidFromMonth is { } vm && vm is < 1 or > 12) Add("validFromMonth", "Yürürlük ayı 1–12 olmalı");

        if (i.MinimumWageGross is null) Add("minimumWageGross", "Zorunlu alan");
        else if (i.MinimumWageGross <= 0 || i.MinimumWageGross > 10_000_000) Add("minimumWageGross", "Brüt asgari ücret sıfırdan büyük olmalı");
        if (i.MinimumWageNet is { } net && (net <= 0 || (i.MinimumWageGross is { } g && net > g)))
            Add("minimumWageNet", "Net asgari ücret sıfırdan büyük ve brütten küçük olmalı");

        Rate("sgkEmployeeRate", i.SgkEmployeeRate, 0.5m, "SGK işçi oranı");
        Rate("unemploymentEmployeeRate", i.UnemploymentEmployeeRate, 0.1m, "İşsizlik işçi oranı");
        Rate("sgkEmployerRate", i.SgkEmployerRate, 0.5m, "SGK işveren oranı");
        Rate("unemploymentEmployerRate", i.UnemploymentEmployerRate, 0.1m, "İşsizlik işveren oranı");
        Rate("stampTaxRate", i.StampTaxRate, 0.1m, "Damga vergisi oranı");

        if (i.EmployerIncentivePoints is null) Add("employerIncentivePoints", "Zorunlu alan (teşvik yoksa 0)");
        else if (i.EmployerIncentivePoints is < 0 or > 20) Add("employerIncentivePoints", "Teşvik indirimi 0–20 puan olmalı");
        else if (i.SgkEmployerRate is { } er && i.EmployerIncentivePoints / 100m > er) Add("employerIncentivePoints", "Teşvik indirimi işveren SGK oranını aşamaz");

        if (i.SgkCeilingMultiplier is null) Add("sgkCeilingMultiplier", "Zorunlu alan");
        else if (i.SgkCeilingMultiplier < 1) Add("sgkCeilingMultiplier", "SGK tavanı tabandan (asgari ücret) düşük olamaz");
        else if (i.SgkCeilingMultiplier > 20) Add("sgkCeilingMultiplier", "SGK tavanı asgari ücretin en fazla 20 katı olabilir");

        var br = i.Brackets ?? new List<TaxBracket>();
        if (br.Count is < 1 or > 10) Add("brackets", "1–10 gelir vergisi dilimi girin");
        else
        {
            if (br[^1].UpTo is not null) Add("brackets", "Son dilimin üst sınırı boş olmalı (üzeri)");
            if (br.Take(br.Count - 1).Any(b => b.UpTo is null or <= 0)) Add("brackets", "Son dilim dışındaki dilimlerin üst sınırı girilmeli");
            else if (br.Take(br.Count - 1).Zip(br.Skip(1).Take(br.Count - 2)).Any(x => x.First.UpTo >= x.Second.UpTo))
                Add("brackets", "Dilim üst sınırları artan sırada olmalı");
            if (br.Any(b => b.Rate is < 0 or > 1)) Add("brackets", "Dilim oranları %0 ile %100 arasında olmalı");
            else if (br.Zip(br.Skip(1)).Any(x => x.Second.Rate < x.First.Rate)) Add("brackets", "Dilim oranları azalmamalı");
        }

        if (i.SeveranceCeilingH1 is <= 0) Add("severanceCeilingH1", "Kıdem tavanı sıfırdan büyük olmalı (bilinmiyorsa boş bırakın)");
        if (i.SeveranceCeilingH2 is <= 0) Add("severanceCeilingH2", "Kıdem tavanı sıfırdan büyük olmalı (bilinmiyorsa boş bırakın)");
        if (i.AgiMonthly is < 0) Add("agiMonthly", "AGİ negatif olamaz");
        if (i.Source is { Length: > 300 }) Add("source", "Kaynak notu en fazla 300 karakter olabilir");
        return e;
    }
}
