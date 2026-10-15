using CompensationService.Auditing;
using CompensationService.Payroll;
using Xunit;

namespace Compensation.Tests;

/// <summary>Güvenlik dalgası 2B: bordroyu hazırlayan kapatamaz; IBAN'ı değiştiren o bordroyu kapatamaz.</summary>
public class SegregationOfDutiesTests
{
    static SegregationOfDuties.CloseCheck Check(string closer, string? calc = "hazirlayan", string[]? authors = null, int iban = 0) =>
        new(closer, calc, authors ?? Array.Empty<string>(), iban);

    [Fact]
    public void Hesaplayan_kapatamaz()
    {
        var v = SegregationOfDuties.Evaluate(Check("hazirlayan"), enforced: true);
        Assert.Equal(SegregationOfDuties.SameUserCode, v?.Code);
        Assert.Contains("Görevler ayrılığı", v!.Message);
    }

    [Fact]
    public void Elle_kalem_giren_kapatamaz()
    {
        var v = SegregationOfDuties.Evaluate(Check("onaylayan", authors: new[] { "onaylayan" }), enforced: true);
        Assert.Equal(SegregationOfDuties.SameUserCode, v?.Code);
    }

    [Fact]
    public void Iban_degistiren_ilk_uygulandigi_bordroyu_kapatamaz()
    {
        var v = SegregationOfDuties.Evaluate(Check("onaylayan", iban: 2), enforced: true);
        Assert.Equal(SegregationOfDuties.IbanEditorCode, v?.Code);
        Assert.Contains("2 çalışanın", v!.Message);
    }

    [Fact]
    public void Farkli_kisi_kapatabilir_ve_kural_kapaliysa_serbest()
    {
        Assert.Null(SegregationOfDuties.Evaluate(Check("onaylayan", authors: new[] { "hazirlayan" }), enforced: true));
        Assert.Null(SegregationOfDuties.Evaluate(Check("hazirlayan", iban: 3), enforced: false));
        // Eski dönemler (hazırlayan kaydı yok) engellenmez.
        Assert.Null(SegregationOfDuties.Evaluate(Check("biri", calc: null), enforced: true));
    }

    [Fact]
    public void Iz_kodu_bicimi() => Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", TraceCode.New());
}
