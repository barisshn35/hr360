using System.Security.Claims;
using System.Text.Json;
using ExpenseService.Controllers;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;
using ExpenseService.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Expense.Tests;

/// <summary>Sohbet botunun iç uçları: anahtar, kiracı, web ucuyla aynı doğrulama.</summary>
public class InternalChatTests
{
    private const string Token = "unit-test-internal-token";
    static InternalChatTests() => Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);

    private static readonly Guid Emp = Guid.NewGuid();

    private static (InternalChatController Ctl, ExpenseDbContext Db, TenantContext Tenant) Make(string? token = Token)
    {
        var tenant = new TenantContext();
        var db = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseInMemoryDatabase("exp-" + Guid.NewGuid()).Options, tenant);
        var ctl = new InternalChatController(db, new FxService(new HttpClient(), NullLogger<FxService>.Instance), tenant);
        var http = new DefaultHttpContext();
        if (token is not null) http.Request.Headers["X-Internal-Token"] = token;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, db, tenant);
    }

    private static ChatExpenseDraftRequest Draft(decimal amount = 245.50m, DateOnly? date = null, string? category = "Meal", string? tenant = "demo") =>
        new(tenant, Emp, "Fiş 01.10.2026", "TRY", amount, date ?? DateOnly.FromDateTime(DateTime.UtcNow), category, "Sohbetten (fiş okuma)", "Slack");

    private static string? Message(IActionResult r) =>
        r is ObjectResult { Value: { } v } ? JsonSerializer.SerializeToElement(v).TryGetProperty("message", out var m) ? m.GetString() : null : null;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task Anahtar_yoksa_ya_da_yanlissa_404(string? token)
    {
        var (ctl, db, _) = Make(token);
        Assert.IsType<NotFoundResult>(await ctl.ExpenseDraft(Draft(), default));
        Assert.IsType<NotFoundResult>(await ctl.HrCase(new("demo", Emp, "Konu", "Açıklama"), default));
        Assert.Equal(0, await db.Claims.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Taslak_beyan_tek_kalemle_kiraciya_yazilir()
    {
        var (ctl, db, _) = Make();
        var r = Assert.IsType<OkObjectResult>(await ctl.ExpenseDraft(Draft(), default));
        var body = JsonSerializer.SerializeToElement(r.Value);
        Assert.Equal("Draft", body.GetProperty("status").GetString());
        Assert.Equal(245.50m, body.GetProperty("totalAmount").GetDecimal());
        var claim = await db.Claims.IgnoreQueryFilters().Include(c => c.Items).SingleAsync();
        Assert.Equal(body.GetProperty("id").GetGuid(), claim.Id);
        Assert.Equal("demo", claim.TenantSlug);
        Assert.Equal(Emp, claim.EmployeeId);
        Assert.Equal(ClaimStatus.Draft, claim.Status);
        var item = Assert.Single(claim.Items);
        Assert.Equal(ExpenseCategory.Meal, item.Category);
        Assert.Equal("demo", item.TenantSlug);
        // Denetim kaydında işlemi yapan: sohbetteki çalışan.
        Assert.Equal("employee:" + Emp, ctl.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Sohbet (Slack)", ctl.HttpContext.User.FindFirst("name")?.Value);
    }

    [Fact]
    public async Task Web_ucunun_kurallari_gecerli_tutar_tarih_kategori()
    {
        var (ctl, db, _) = Make();
        var tooMuch = Assert.IsType<ObjectResult>(await ctl.ExpenseDraft(Draft(amount: 1_000_001m), default));
        Assert.Equal(400, tooMuch.StatusCode);
        Assert.Equal("Kalem tutarı en fazla 1.000.000 olabilir", Message(tooMuch));
        var zero = await ctl.ExpenseDraft(Draft(amount: 0), default);
        Assert.Equal("Kalem tutari sifirdan buyuk olmali", Message(zero));
        var future = await ctl.ExpenseDraft(Draft(date: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))), default);
        Assert.Equal("Harcama tarihi gelecekte olamaz", Message(future));
        Assert.IsType<BadRequestObjectResult>(await ctl.ExpenseDraft(Draft(category: "Uzay"), default));
        Assert.IsType<BadRequestObjectResult>(await ctl.ExpenseDraft(Draft(category: "3"), default));
        // Kilometre kalemi km ister (web kuralı) — sohbetten tutarla girilemez.
        Assert.Equal("Kilometre 0–10000 arasında olmalı", Message(await ctl.ExpenseDraft(Draft(category: "Mileage"), default)));
        Assert.IsType<BadRequestObjectResult>(await ctl.ExpenseDraft(Draft(tenant: " "), default));
        Assert.Equal(0, await db.Claims.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Ik_vakasi_acilir_konu_kurali_uygulanir()
    {
        var (ctl, db, _) = Make();
        var ok = Assert.IsType<OkObjectResult>(await ctl.HrCase(new("demo", Emp, "Sohbetten soru: izin", "İzin bakiyem neden eksik?"), default));
        Assert.Equal("Open", JsonSerializer.SerializeToElement(ok.Value).GetProperty("status").GetString());
        var c = await db.Cases.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(("demo", Emp, CaseCategory.Other, CasePriority.Normal, CaseStatus.Open), (c.TenantSlug, c.EmployeeId, c.Category, c.Priority, c.Status));

        var longSubject = await ctl.HrCase(new("demo", Emp, new string('x', 201), "d"), default);
        Assert.Equal("Konu zorunlu ve en fazla 200 karakter olabilir", Message(longSubject));
        Assert.IsType<BadRequestObjectResult>(await ctl.HrCase(new("demo", Guid.Empty, "Konu", "d"), default));
        Assert.Equal(1, await db.Cases.IgnoreQueryFilters().CountAsync());
    }
}
