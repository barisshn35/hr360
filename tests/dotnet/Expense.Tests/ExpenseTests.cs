using ExpenseService.Models;
using ExpenseService.Services;
using Xunit;

namespace Expense.Tests;

public class ExpensePolicyTests
{
    private static ExpenseItem I(ExpenseCategory c, decimal a, string? receipt = null) =>
        new() { Category = c, Amount = a, ExpenseDate = new DateOnly(2026, 10, 1), ReceiptStorageKey = receipt };

    [Fact]
    public void Kalem_ve_aylik_limit_ile_fis_zorunlulugu()
    {
        var limits = new Dictionary<string, CategoryLimit> { ["Meal"] = new(500, 1000, 200) };
        var mtd = new Dictionary<ExpenseCategory, decimal> { [ExpenseCategory.Meal] = 600 };
        var v = ExpensePolicies.Violations(new[] { I(ExpenseCategory.Meal, 550), I(ExpenseCategory.Meal, 100) }, limits, mtd);
        Assert.Contains(v, x => x.Contains("kalem limiti"));
        Assert.Contains(v, x => x.Contains("fiş"));
        Assert.Contains(v, x => x.Contains("aylık"));
    }

    [Fact]
    public void Limit_icinde_ihlal_yok()
    {
        var limits = new Dictionary<string, CategoryLimit> { ["Meal"] = new(500, 1000, 200) };
        Assert.Empty(ExpensePolicies.Violations(new[] { I(ExpenseCategory.Meal, 300, "fis.jpg"), I(ExpenseCategory.Travel, 99999) }, limits, new Dictionary<ExpenseCategory, decimal>()));
    }

    [Fact]
    public void Harcirah_gunu_gidis_donus_dahil() => Assert.Equal(3, ExpensePolicies.PerDiemDays(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7)));

    [Fact]
    public void Fis_metninden_tutar_tarih_vkn()
    {
        var text = "ÖRNEK KAFE LTD\nVKN: 1234567890\nTARİH 03.10.2026 SAAT 12:41\nKDV %10 12,73\nARA TOPLAM 127,27\nTOPLAM *140,00\nKREDİ KARTI ****1234";
        var (amount, date, vkn) = ExpensePolicies.ParseReceipt(text);
        Assert.Equal(140.00m, amount);
        Assert.Equal(new DateOnly(2026, 10, 3), date);
        Assert.Equal("1234567890", vkn);
    }

    [Fact]
    public void Binlik_ayracli_tutar() => Assert.Equal(1250.50m, ExpensePolicies.ParseReceipt("GENEL TOPLAM 1.250,50").Amount);

    [Fact]
    public void Tcmb_xml_ayristirma()
    {
        var xml = """<?xml version="1.0" encoding="UTF-8"?><Tarih_Date><Currency CrossOrder="0" Kod="USD" CurrencyCode="USD"><Unit>1</Unit><ForexBuying>41.5021</ForexBuying></Currency><Currency Kod="JPY" CurrencyCode="JPY"><Unit>100</Unit><ForexBuying>27.8</ForexBuying></Currency></Tarih_Date>""";
        var r = FxService.Parse(xml);
        Assert.Equal(41.5021m, r["USD"]);
        Assert.Equal(0.278m, r["JPY"]);
    }
}
