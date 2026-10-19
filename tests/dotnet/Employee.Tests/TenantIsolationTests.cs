using EmployeeService.Data;
using EmployeeService.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Employee.Tests;

/// <summary>EF kiracı süzgeci: bir şirketin oturumu başka şirketin çalışanını görmez; kayıt oturumun şirketiyle damgalanır.</summary>
public class TenantIsolationTests
{
    static EmployeeDbContext Db(string dbName, string? tenant, bool platform = false) =>
        new(new DbContextOptionsBuilder<EmployeeDbContext>().UseInMemoryDatabase(dbName).Options,
            new TenantContext { TenantSlug = tenant, IsPlatformAdmin = platform });

    static EmployeeService.Models.Employee New(string first) =>
        new() { FirstName = first, LastName = "X", Email = $"{first}@example.com", HireDate = new DateOnly(2024, 1, 1) };

    [Fact]
    public async Task Each_tenant_sees_only_its_own_rows()
    {
        var name = Guid.NewGuid().ToString();
        await using (var a = Db(name, "sirket-a")) { a.Employees.Add(New("Ali")); await a.SaveChangesAsync(); }
        await using (var b = Db(name, "sirket-b")) { b.Employees.Add(New("Berk")); await b.SaveChangesAsync(); }

        await using (var a = Db(name, "sirket-a"))
            Assert.Equal(new[] { "Ali" }, await a.Employees.Select(e => e.FirstName).ToListAsync());
        await using (var b = Db(name, "sirket-b"))
        {
            var berk = Assert.Single(await b.Employees.ToListAsync());
            Assert.Equal("sirket-b", berk.TenantSlug);
        }
        await using (var p = Db(name, null, platform: true))
            Assert.Equal(2, await p.Employees.CountAsync());
    }

    [Fact]
    public async Task Unresolved_tenant_sees_nothing()
    {
        var name = Guid.NewGuid().ToString();
        await using (var a = Db(name, "sirket-a")) { a.Employees.Add(New("Ali")); await a.SaveChangesAsync(); }
        await using var anon = Db(name, null);
        Assert.Equal(0, await anon.Employees.CountAsync());
    }
}
