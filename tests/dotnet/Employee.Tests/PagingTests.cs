using EmployeeService.Data;
using EmployeeService.Infrastructure;
using EmployeeService.Models;
using EmployeeService.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Employee.Tests;

/// <summary>Liste sayfalaması (Infrastructure/Paging.cs): geriye uyumlu düz dizi, sayfa sınırları, Türkçe arama deseni.</summary>
public class PagingTests
{
    sealed class Probe : ControllerBase { }

    static Probe Controller() => new() { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    static async Task<EmployeeDbContext> Seed(int n)
    {
        var tenant = new TenantContext { TenantSlug = "t-paging" };
        var db = new EmployeeDbContext(new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        for (var i = 0; i < n; i++)
            db.Employees.Add(new EmployeeService.Models.Employee
            {
                FirstName = $"Ad{i:D3}", LastName = "Soyad", Email = $"k{i}@example.com", HireDate = new DateOnly(2020, 1, 1).AddDays(i),
            });
        await db.SaveChangesAsync();
        return db;
    }

    [Theory]
    [InlineData(0, null, 1, Paging.DefaultPageSize)]
    [InlineData(-5, 0, 1, 1)]
    [InlineData(3, 50, 3, 50)]
    [InlineData(2, 10_000, 2, Paging.MaxPageSize)]
    [InlineData(int.MaxValue, 20, 1_000_000, 20)]
    public void Normalize_clamps(int page, int? size, int wantPage, int wantSize) =>
        Assert.Equal((wantPage, wantSize), Paging.Normalize(page, size));

    [Fact]
    public async Task Without_page_returns_flat_list_and_total_header()
    {
        await using var db = await Seed(7);
        var c = Controller();
        var r = Assert.IsType<OkObjectResult>(await Paging.ListAsync(c, db.Employees.OrderBy(e => e.FirstName), null, null, default));
        Assert.Equal(7, Assert.IsAssignableFrom<List<EmployeeService.Models.Employee>>(r.Value).Count);
        Assert.Equal("7", c.Response.Headers["X-Total-Count"].ToString());
    }

    [Fact]
    public async Task Pages_do_not_overlap_and_cover_everything()
    {
        await using var db = await Seed(25);
        var seen = new List<string>();
        for (var p = 1; p <= 3; p++)
        {
            var c = Controller();
            var r = Assert.IsType<OkObjectResult>(await Paging.ListAsync(c, db.Employees.OrderBy(e => e.FirstName), p, 10, default));
            var page = Assert.IsType<PagedResult<EmployeeService.Models.Employee>>(r.Value);
            Assert.Equal(25, page.Total);
            Assert.Equal(p, page.Page);
            seen.AddRange(page.Items.Select(e => e.FirstName));
        }
        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        Assert.Equal(seen.OrderBy(s => s, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task Page_beyond_end_is_empty_with_total()
    {
        await using var db = await Seed(3);
        var r = Assert.IsType<OkObjectResult>(await Paging.ListAsync(Controller(), db.Employees.OrderBy(e => e.Id), 9, 10, default));
        var page = Assert.IsType<PagedResult<EmployeeService.Models.Employee>>(r.Value);
        Assert.Empty(page.Items);
        Assert.Equal(3, page.Total);
    }

    [Theory]
    [InlineData("AYŞE", "ayse")]
    [InlineData("İsmail Işık", "ismail isik")]
    [InlineData("Çağrı Gün Öz", "cagri gun oz")]
    [InlineData("Hâkim", "hakim")]
    public void Fold_matches_sql_folding(string input, string want) => Assert.Equal(want, Paging.Fold(input));

    [Theory]
    [InlineData("50%", @"%50\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c:\x", @"%c:\\x%")]
    [InlineData("  ali  ", "%ali%")]
    public void Like_escapes_wildcards(string input, string want) => Assert.Equal(want, Paging.Like(input));

    [Fact]
    public void Guids_skips_invalid_and_duplicates_and_caps_at_200()
    {
        var g = Guid.NewGuid();
        Assert.Equal(new[] { g }, Paging.Guids($" {g}, yok ,{g},{Guid.Empty}"));
        Assert.Empty(Paging.Guids(null));
        Assert.Equal(200, Paging.Guids(string.Join(",", Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()))).Count);
    }

    [Fact]
    public void Desc_is_case_insensitive()
    {
        Assert.True(Paging.Desc("DESC"));
        Assert.False(Paging.Desc("asc"));
        Assert.False(Paging.Desc(null));
    }
}
