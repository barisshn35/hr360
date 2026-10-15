using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Controllers;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Preferences;
using NotificationService.Tenancy;
using Xunit;

namespace Notification.Tests;

/// <summary>Dalga 12: arayüz tercihleri (kayıtlı görünüm, panel düzeni, Yenilikler).</summary>
public class UiPrefRulesTests
{
    [Theory]
    [InlineData("dashboard", true)]
    [InlineData("views:leave-requests", true)]
    [InlineData("whatsnew", true)]
    [InlineData("a.b_c:d-e", true)]
    [InlineData("", false)]
    [InlineData("Dashboard", false)]
    [InlineData("-dashboard", false)]
    [InlineData("views/../x", false)]
    [InlineData("çalışan", false)]
    [InlineData(null, false)]
    public void Anahtar_dogrulamasi(string? key, bool ok) => Assert.Equal(ok, UiPrefRules.IsValidKey(key));

    [Fact]
    public void Anahtar_en_fazla_100_karakter()
    {
        Assert.True(UiPrefRules.IsValidKey(new string('a', 100)));
        Assert.False(UiPrefRules.IsValidKey(new string('a', 101)));
    }

    [Fact]
    public void Deger_nesne_ya_da_dizi_olmali()
    {
        Assert.NotNull(UiPrefRules.Normalize(JsonDocument.Parse("42").RootElement).Error);
        Assert.NotNull(UiPrefRules.Normalize(JsonDocument.Parse("\"x\"").RootElement).Error);
        var (json, err) = UiPrefRules.Normalize(JsonDocument.Parse("{ \"order\": [\"a\", \"b\"] }").RootElement);
        Assert.Null(err);
        Assert.Equal("{\"order\":[\"a\",\"b\"]}", json);
        Assert.Null(UiPrefRules.Normalize(JsonDocument.Parse("[]").RootElement).Error);
    }

    [Fact]
    public void Deger_16_KB_siniri()
    {
        var big = JsonSerializer.Serialize(new { s = new string('x', UiPrefRules.MaxValueBytes) });
        Assert.NotNull(UiPrefRules.Normalize(JsonDocument.Parse(big).RootElement).Error);
    }

    static UiPrefsController Make(string? tenant, string? sub, out NotificationDbContext db)
    {
        var ctx = new TenantContext { TenantSlug = tenant };
        db = new NotificationDbContext(new DbContextOptionsBuilder<NotificationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, ctx);
        var claims = sub is null ? Array.Empty<Claim>() : new[] { new Claim("sub", sub) };
        return new UiPrefsController(db, ctx)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    [Fact]
    public async Task Kiraci_yoksa_liste_204_yazim_409()
    {
        var c = Make(null, "u1", out _);
        Assert.IsType<NoContentResult>(await c.All(default));
        Assert.IsType<ConflictObjectResult>(await c.Get("dashboard", default));
    }

    [Fact]
    public async Task Yalnizca_kendi_tercihleri_okunur()
    {
        var c = Make("demo", "u1", out var db);
        db.UiPreferences.AddRange(
            new UiPreference { Id = Guid.NewGuid(), TenantSlug = "demo", UserSub = "u1", Key = "dashboard", Value = "{\"hidden\":[\"chart\"]}" },
            new UiPreference { Id = Guid.NewGuid(), TenantSlug = "demo", UserSub = "u2", Key = "dashboard", Value = "{\"hidden\":[]}" },
            new UiPreference { Id = Guid.NewGuid(), TenantSlug = "other", UserSub = "u1", Key = "whatsnew", Value = "{\"seen\":\"1\"}" });
        await db.SaveChangesAsync();
        var ok = Assert.IsType<OkObjectResult>(await c.All(default));
        var dict = Assert.IsType<Dictionary<string, JsonElement>>(ok.Value);
        Assert.Single(dict);
        Assert.Equal("chart", dict["dashboard"].GetProperty("hidden")[0].GetString());
        Assert.IsType<NoContentResult>(await c.Get("whatsnew", default));
        Assert.IsType<BadRequestObjectResult>(await c.Get("Bad Key", default));
    }
}
