using PerformanceService.Services;
using Xunit;

namespace Performance.Tests;

/// <summary>G12 9-kutu eşlemesi ve dönem şablonu doğrulaması.</summary>
public class NineBoxTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(54.99, 1)]
    [InlineData(55, 2)]
    [InlineData(74.99, 2)]
    [InlineData(75, 3)]
    [InlineData(100, 3)]
    public void Performans_bandi(double score, int band) =>
        Assert.Equal(band, NineBoxMath.PerformanceBand((decimal)score, 55m, 75m));

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 1, 3)]
    [InlineData(1, 3, 7)]
    [InlineData(2, 2, 5)]
    [InlineData(3, 3, 9)]
    public void Hucre_numarasi(int perf, int pot, int cell)
    {
        Assert.Equal(cell, NineBoxMath.Cell(perf, pot));
        Assert.Equal((perf, pot), NineBoxMath.Bands(cell));
        Assert.False(string.IsNullOrEmpty(NineBoxMath.Label(cell)));
    }

    [Fact]
    public void Gecersiz_bant_reddedilir()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NineBoxMath.Cell(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NineBoxMath.Cell(2, 4));
    }

    private static CycleConfig Config(params int[] weights) => new()
    {
        Sections = weights.Select((w, i) => new CycleConfig.SectionConfig
        {
            Title = $"B{i}", Weight = w, Questions = new() { new CycleConfig.QuestionConfig { Text = "Soru" } },
        }).ToList(),
    };

    [Fact]
    public void Sablon_agirlik_toplami_100_olmali()
    {
        Assert.Null(Config(60, 40).Validate());
        Assert.Contains("100", Config(60, 30).Validate());
        Assert.Null(new CycleConfig().Validate()); // bölümsüz şablon (metrik tanımları geçerli)
    }

    [Fact]
    public void Sablon_olcek_ve_soru_kurallari()
    {
        var c = Config(100);
        c.Scale = new CycleConfig.ScaleConfig { Min = 5, Max = 1 };
        Assert.NotNull(c.Validate());
        c = Config(100);
        c.Sections[0].Questions.Clear();
        Assert.NotNull(c.Validate());
    }

    [Fact]
    public void Sablon_json_gidis_donus()
    {
        var c = Config(70, 30);
        c.Scale.Labels = new() { "a", "b" };
        var back = CycleConfig.Parse(c.ToJson())!;
        Assert.Equal(70, back.Sections[0].Weight);
        Assert.Equal("b", back.Scale.Labels![1]);
        Assert.Null(CycleConfig.Parse("{bozuk"));
    }
}
