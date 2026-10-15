using OnboardingService.Services;
using Xunit;

namespace Onboarding.Tests;

/// <summary>G14 karşılama şablonu, G16 QR kodu.</summary>
public class WelcomeTests
{
    [Fact]
    public void Yer_tutucular_doldurulur()
    {
        var s = WelcomeTemplate.Render("Merhaba {ad}, {baslangic} — yönetici {yonetici}, buddy {buddy}, yer {konum}",
            "Ayşe", new DateOnly(2026, 10, 12), "Mehmet Kaya", "Can Yılmaz", "İstanbul ofis");
        Assert.Equal("Merhaba Ayşe, 12.10.2026 — yönetici Mehmet Kaya, buddy Can Yılmaz, yer İstanbul ofis", s);
    }

    [Fact]
    public void Eksik_bilgi_yerine_ik_yazilir()
    {
        var s = WelcomeTemplate.Render("{yonetici}|{buddy}|{konum}|{name}", "Ali", new DateOnly(2026, 1, 1), null, " ", null);
        Assert.Equal("İK ekibi|İK ekibi|İK ekibi bilgilendirecek|Ali", s);
    }

    [Fact]
    public void Varsayilan_sablonda_hassas_alan_yok()
    {
        var body = WelcomeTemplate.DefaultBody.ToLowerInvariant();
        foreach (var w in new[] { "maaş", "ücret", "tckn", "kimlik", "adres", "iban" }) Assert.DoesNotContain(w, body);
    }

    [Fact]
    public void Qr_kodu_opak_ve_benzersiz()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => AssetCodes.New()).ToList();
        Assert.All(codes, c => Assert.Matches("^[2-9A-HJ-NP-Z]{16}$", c));
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal("ABCD2345EFGH6789", AssetCodes.Normalize(" abcd-2345 efgh-6789 "));
    }
}
