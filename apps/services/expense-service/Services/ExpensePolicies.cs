using System.Text.Json;
using ExpenseService.Models;

namespace ExpenseService.Services;

public sealed record CategoryLimit(decimal? PerItem, decimal? Monthly, decimal? ReceiptAbove);

/// <summary>G9 masraf politikası denetimi (saf fonksiyonlar; birim testli).</summary>
public static class ExpensePolicies
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Dictionary<string, CategoryLimit> Limits(ExpensePolicy? p) =>
        p is null ? new() : JsonSerializer.Deserialize<Dictionary<string, CategoryLimit>>(p.LimitsJson, Json) ?? new();

    /// <summary>
    /// Beyanın kalemlerini politikaya göre denetler. monthToDate: aynı çalışanın bu ay
    /// gönderilmiş/onaylanmış diğer beyanlarındaki kategori toplamları (TL).
    /// </summary>
    public static List<string> Violations(IEnumerable<ExpenseItem> items, Dictionary<string, CategoryLimit> limits, IReadOnlyDictionary<ExpenseCategory, decimal> monthToDate)
    {
        var v = new List<string>();
        foreach (var g in items.GroupBy(i => i.Category))
        {
            if (!limits.TryGetValue(g.Key.ToString(), out var l)) continue;
            foreach (var i in g)
            {
                if (l.PerItem is { } per && i.Amount > per)
                    v.Add($"{Label(g.Key)}: kalem tutarı {i.Amount:0.##} TL, kalem limiti {per:0.##} TL");
                if (l.ReceiptAbove is { } ra && i.Amount > ra && string.IsNullOrWhiteSpace(i.ReceiptStorageKey))
                    v.Add($"{Label(g.Key)}: {ra:0.##} TL üzerindeki harcama için fiş/fatura eklenmeli");
            }
            if (l.Monthly is { } m)
            {
                var total = g.Sum(i => i.Amount) + monthToDate.GetValueOrDefault(g.Key);
                if (total > m) v.Add($"{Label(g.Key)}: aylık toplam {total:0.##} TL, aylık limit {m:0.##} TL");
            }
        }
        return v;
    }

    public static string Label(ExpenseCategory c) => c switch
    {
        ExpenseCategory.Travel => "Seyahat", ExpenseCategory.Meal => "Yemek", ExpenseCategory.Accommodation => "Konaklama",
        ExpenseCategory.Transport => "Ulaşım", ExpenseCategory.Supplies => "Malzeme", ExpenseCategory.Training => "Eğitim",
        ExpenseCategory.Mileage => "Kilometre", ExpenseCategory.PerDiem => "Harcırah", _ => "Diğer",
    };

    /// <summary>Harcırah günü: gidiş ve dönüş dahil takvim günü.</summary>
    public static int PerDiemDays(DateOnly start, DateOnly end) => Math.Max(0, end.DayNumber - start.DayNumber + 1);

    /// <summary>Basit fiş metni ayrıştırma (OCR çıktısı): TOPLAM tutarı, tarih, VKN.</summary>
    public static (decimal? Amount, DateOnly? Date, string? TaxNo) ParseReceipt(string text)
    {
        decimal? amount = null; DateOnly? date = null; string? vkn = null;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var money = new System.Text.RegularExpressions.Regex(@"(\d{1,3}(?:[.\s]\d{3})*(?:,\d{2})|\d+(?:[.,]\d{2}))");
        foreach (var l in lines)
        {
            var up = l.ToUpper(new System.Globalization.CultureInfo("tr-TR"));
            if ((up.Contains("TOPLAM") || up.Contains("TOP ") || up.Contains("TUTAR")) && !up.Contains("KDV") && !up.Contains("ARA"))
            {
                var ms = money.Matches(l);
                if (ms.Count > 0 && TryMoney(ms[^1].Value, out var a)) amount = a;
            }
            var dm = System.Text.RegularExpressions.Regex.Match(l, @"\b(\d{2})[./-](\d{2})[./-](\d{4})\b");
            if (date is null && dm.Success && DateOnly.TryParseExact($"{dm.Groups[1].Value}.{dm.Groups[2].Value}.{dm.Groups[3].Value}", "dd.MM.yyyy", out var d)) date = d;
            var vm = System.Text.RegularExpressions.Regex.Match(up, @"(?:VKN|V\.?D\.?\s*NO|VERG[İI]\s*NO)\D{0,5}(\d{10,11})");
            if (vkn is null && vm.Success) vkn = vm.Groups[1].Value;
        }
        return (amount, date, vkn);
    }

    private static bool TryMoney(string raw, out decimal value)
    {
        var s = raw.Replace(" ", "");
        if (s.Contains(',')) s = s.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
