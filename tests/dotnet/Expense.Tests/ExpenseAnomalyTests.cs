using System.Text.Json;
using ExpenseService.Controllers;
using ExpenseService.Models;
using ExpenseService.Services;
using Xunit;

namespace Expense.Tests;

/// <summary>Masraf denetimi (ML dalgası 1): ML istek gövdesi, işaretlerin kalemlere yazılması, e-Fatura alanları.</summary>
public class ExpenseAnomalyTests
{
    private static readonly Guid Emp = Guid.Parse("0e879b9e-d72b-489f-aa5b-8291e0bcbefb");

    private static ExpenseItem I(decimal amount, string? desc = null, string? ettn = null) => new()
    {
        Category = ExpenseCategory.Meal, Amount = amount, ExpenseDate = new DateOnly(2026, 10, 2), Description = desc,
        SupplierTaxId = "1234567890", InvoiceNo = "ABC2026000000123", Ettn = ettn,
    };

    [Fact]
    public void Istek_govdesinde_kimlik_ve_aciklama_yok_yalnizca_ozet()
    {
        var hist = new[] { new HistoryRow(Guid.NewGuid(), ExpenseCategory.Meal, 90m, new DateOnly(2026, 9, 1), null, null, null, "Ayşe Yılmaz ile müşteri yemeği") };
        var body = JsonSerializer.Serialize(ExpenseAnomalyClient.BuildRequest("demo", Emp,
            new[] { I(120m, "Müşteri yemeği - Ankara", "f47ac10b-58cc-4372-a567-0e02b2c3d479") }, hist));
        Assert.DoesNotContain(Emp.ToString(), body);
        Assert.DoesNotContain(Emp.ToString("N"), body);
        Assert.DoesNotContain("Ayşe", body);
        Assert.DoesNotContain("Ankara", body);
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("0", item.GetProperty("id").GetString());
        Assert.Equal("Meal", item.GetProperty("category").GetString());
        Assert.Equal("2026-10-02", item.GetProperty("date").GetString());
        Assert.Equal(ExpenseAnomalyClient.Pseudonym("demo", Emp), item.GetProperty("employee").GetString());
        Assert.Equal(16, item.GetProperty("text_hash").GetString()!.Length);
        Assert.Equal(1, doc.RootElement.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public void Takma_ad_kiraciya_ozgu_ve_aciklama_ozeti_normalize()
    {
        Assert.NotEqual(ExpenseAnomalyClient.Pseudonym("demo", Emp), ExpenseAnomalyClient.Pseudonym("other", Emp));
        Assert.Equal(ExpenseAnomalyClient.TextHash("Müşteri  yemeği!"), ExpenseAnomalyClient.TextHash("müşteri yemeği"));
        Assert.Null(ExpenseAnomalyClient.TextHash("taksi"));
        Assert.Null(ExpenseAnomalyClient.TextHash(null));
    }

    [Fact]
    public void Isaretler_istek_sirasina_gore_kalemlere_yazilir()
    {
        var items = new[] { I(100m), I(5000m), I(80m) };
        items[0].AnomalyFlagsJson = "[{\"code\":\"OLD\"}]";
        using var doc = JsonDocument.Parse("""
            {"items":[{"id":"0","flags":[]},{"id":"1","flags":[{"code":"AMOUNT_OUTLIER_CATEGORY","severity":"low","reason":"x","details":{"z":5}}]},
                      {"id":"9","flags":[{"code":"X"}]}]}
            """);
        Assert.Equal(1, ExpenseAnomalyClient.ApplyFlags(items, doc.RootElement));
        Assert.Null(items[0].AnomalyFlagsJson);
        Assert.Equal("AMOUNT_OUTLIER_CATEGORY", items[1].AnomalyFlags!.Value[0].GetProperty("code").GetString());
        Assert.Null(items[2].AnomalyFlags);
    }

    [Fact]
    public void Gecersiz_yanit_isaret_yazmaz()
    {
        var items = new[] { I(100m) };
        using var doc = JsonDocument.Parse("{\"detail\":\"x\"}");
        Assert.Equal(0, ExpenseAnomalyClient.ApplyFlags(items, doc.RootElement));
        Assert.Null(items[0].AnomalyFlagsJson);
    }

    private static ExpenseItemInput In(string? vkn = null, string? no = null, string? ettn = null) =>
        new(ExpenseCategory.Meal, 10m, new DateOnly(2026, 10, 1), null, null, SupplierTaxId: vkn, InvoiceNo: no, Ettn: ettn);

    [Fact]
    public void Efatura_alanlari_dogrulanir_ve_normalize_edilir()
    {
        var (err, vkn, no, ettn) = ExpenseClaimsController.NormalizeInvoiceFields(In(" 1234567890 ", "abc2026000000123", "F47AC10B-58CC-4372-A567-0E02B2C3D479"));
        Assert.Null(err);
        Assert.Equal("1234567890", vkn);
        Assert.Equal("ABC2026000000123", no);
        Assert.Equal("f47ac10b-58cc-4372-a567-0e02b2c3d479", ettn);
        Assert.NotNull(ExpenseClaimsController.NormalizeInvoiceFields(In("12345")).Error);
        Assert.NotNull(ExpenseClaimsController.NormalizeInvoiceFields(In(no: "ABC-1")).Error);
        Assert.NotNull(ExpenseClaimsController.NormalizeInvoiceFields(In(ettn: "xyz")).Error);
        Assert.Null(ExpenseClaimsController.NormalizeInvoiceFields(In()).Error);
    }

    [Fact]
    public void Ayni_ETTN_ikinci_kez_gonderilemez()
    {
        const string a = "f47ac10b-58cc-4372-a567-0e02b2c3d479", b = "0e879b9e-d72b-489f-aa5b-8291e0bcbefb";
        // Başka bir (reddedilmemiş) beyanda kullanılmış.
        Assert.Equal(new[] { a }, ExpenseClaimsController.DuplicateEttns(new[] { a, null, b }, new[] { a.ToUpperInvariant() }));
        // Aynı beyanda iki kalemde.
        Assert.Equal(new[] { b }, ExpenseClaimsController.DuplicateEttns(new[] { b, b }, Array.Empty<string>()));
        // Önceki beyan reddedildiyse (sorgu onu getirmez) serbest.
        Assert.Empty(ExpenseClaimsController.DuplicateEttns(new[] { a, b }, Array.Empty<string>()));
        Assert.Empty(ExpenseClaimsController.DuplicateEttns(new string?[] { null, "" }, new[] { a }));
    }
}
