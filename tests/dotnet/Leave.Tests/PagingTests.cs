using LeaveService.Infrastructure;
using Xunit;

namespace Leave.Tests;

/// <summary>G24 sunucu tarafı sayfalama yardımcıları (employee-service'te aynı kopya var).</summary>
public class PagingTests
{
    [Theory]
    [InlineData("Ayşe", "ayse")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("IŞIK Çağlar", "isik caglar")]
    [InlineData("Görüşme ÖZÜ", "gorusme ozu")]
    public void Fold_matches_sql_hr360_fold(string input, string expected) =>
        Assert.Equal(expected, Paging.Fold(input));

    [Fact]
    public void Like_escapes_wildcards()
    {
        Assert.Equal("%a\\%b\\_c\\\\d%", Paging.Like(" a%b_c\\d "));
        Assert.Equal("%ayse%", Paging.FoldLike("Ayşe"));
    }

    [Fact]
    public void Guids_skips_invalid_and_duplicates()
    {
        var g = Guid.NewGuid();
        var list = Paging.Guids($"{g}, x ,{g},,{Guid.Empty}");
        Assert.Equal(new[] { g }, list);
        Assert.Empty(Paging.Guids(null));
    }

    [Fact]
    public void Desc_is_case_insensitive()
    {
        Assert.True(Paging.Desc("DESC"));
        Assert.False(Paging.Desc(null));
        Assert.False(Paging.Desc("asc"));
    }

    [Theory]
    [InlineData(1, null, 1, 20)]
    [InlineData(0, 500, 1, 200)]
    [InlineData(-3, 0, 1, 1)]
    [InlineData(7, 50, 7, 50)]
    public void Normalize_clamps_page_and_size(int page, int? size, int expectedPage, int expectedSize) =>
        Assert.Equal((expectedPage, expectedSize), Paging.Normalize(page, size));
}
