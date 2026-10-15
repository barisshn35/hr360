using ExpenseService.Controllers;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Expense.Tests;

/// <summary>
/// Taslak düzenleme (PUT api/expense-claims/{id}): kalemlerin yenileriyle değiştirilmesi.
/// Önceden yeni kalemler (Id dolu) yalnızca gezinme koleksiyonuna eklendiği için EF UPDATE
/// çalıştırıyor, SaveChanges DbUpdateConcurrencyException atıyordu (her düzenleme 500).
/// </summary>
public class DraftEditTests
{
    private static ExpenseDbContext Ctx(string name) =>
        new(new DbContextOptionsBuilder<ExpenseDbContext>().UseInMemoryDatabase(name).Options,
            new TenantContext { TenantSlug = "demo" });

    private static ExpenseItem Item(decimal amount, string desc) => new()
    {
        Category = ExpenseCategory.Meal, Amount = amount, ExpenseDate = new DateOnly(2026, 10, 1), Description = desc,
    };

    private static async Task<Guid> SeedAsync(string name, params ExpenseItem[] items)
    {
        await using var db = Ctx(name);
        var claim = new ExpenseClaim { EmployeeId = Guid.NewGuid(), Title = "Taslak", Currency = "TRY" };
        claim.Items.AddRange(items);
        claim.TotalAmount = items.Sum(i => i.Amount);
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return claim.Id;
    }

    /// <summary>Ayrı bir bağlamda (yeni istek gibi) kalemleri değiştirip kaydeder.</summary>
    private static async Task ReplaceAsync(string name, Guid id, params ExpenseItem[] items)
    {
        await using var db = Ctx(name);
        var claim = await db.Claims.Include(c => c.Items).FirstAsync(c => c.Id == id);
        ExpenseClaimsController.ReplaceItems(db, claim, items.ToList());
        await db.SaveChangesAsync();
    }

    private static async Task<ExpenseClaim> LoadAsync(string name, Guid id)
    {
        await using var db = Ctx(name);
        return await db.Claims.AsNoTracking().Include(c => c.Items).FirstAsync(c => c.Id == id);
    }

    [Fact]
    public async Task Kalem_eklenir()
    {
        var name = "draft-" + Guid.NewGuid();
        var id = await SeedAsync(name, Item(100, "a"));
        await ReplaceAsync(name, id, Item(100, "a"), Item(50.25m, "b"));
        var c = await LoadAsync(name, id);
        Assert.Equal(2, c.Items.Count);
        Assert.Equal(150.25m, c.TotalAmount);
        Assert.All(c.Items, i => Assert.Equal("demo", i.TenantSlug));
    }

    [Fact]
    public async Task Kalem_cikarilir()
    {
        var name = "draft-" + Guid.NewGuid();
        var id = await SeedAsync(name, Item(100, "a"), Item(40, "b"));
        await ReplaceAsync(name, id, Item(40, "b"));
        var c = await LoadAsync(name, id);
        Assert.Single(c.Items);
        Assert.Equal(40m, c.TotalAmount);
        await using var db = Ctx(name);
        Assert.Equal(1, await db.Items.CountAsync());
    }

    [Fact]
    public async Task Kalem_degistirilir()
    {
        var name = "draft-" + Guid.NewGuid();
        var id = await SeedAsync(name, Item(100, "a"));
        await ReplaceAsync(name, id, Item(123.45m, "a (düzeltildi)"));
        var c = await LoadAsync(name, id);
        var only = Assert.Single(c.Items);
        Assert.Equal(123.45m, only.Amount);
        Assert.Equal("a (düzeltildi)", only.Description);
        Assert.Equal(123.45m, c.TotalAmount);
    }
}
