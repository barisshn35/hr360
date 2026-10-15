using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OnboardingService.Controllers;
using OnboardingService.Data;
using OnboardingService.Models;
using OnboardingService.Services;
using OnboardingService.Tenancy;
using Xunit;

namespace Onboarding.Tests;

/// <summary>Demirbaş düzenleme, elle durum değişikliği ve silme kuralları.</summary>
public class AssetCrudTests
{
    private sealed record World(AssetsController Ctl, OnboardingDbContext Db, Guid Free, Guid Assigned, Guid WithHistory, Guid WithMaintenance);

    private static async Task<World> MakeAsync()
    {
        var tenant = new TenantContext { TenantSlug = "demo" };
        var db = new OnboardingDbContext(new DbContextOptionsBuilder<OnboardingDbContext>()
            .UseInMemoryDatabase("asset-" + Guid.NewGuid()).Options, tenant);
        var free = new Asset { AssetTag = "DMB-001", Type = AssetType.Laptop };
        var assigned = new Asset { AssetTag = "DMB-002", Type = AssetType.Phone, Status = AssetStatus.Assigned };
        var history = new Asset { AssetTag = "DMB-003", Type = AssetType.Monitor };
        var maint = new Asset { AssetTag = "DMB-004", Type = AssetType.Other };
        db.AddRange(free, assigned, history, maint);
        db.AssetAssignments.Add(new AssetAssignment { AssetId = assigned.Id, EmployeeId = Guid.NewGuid(), AssignedOn = new DateOnly(2026, 9, 1) });
        db.AssetAssignments.Add(new AssetAssignment { AssetId = history.Id, EmployeeId = Guid.NewGuid(), AssignedOn = new DateOnly(2026, 1, 1), ReturnedOn = new DateOnly(2026, 6, 1) });
        db.AssetMaintenance.Add(new AssetMaintenance { AssetId = maint.Id, Date = new DateOnly(2026, 5, 1) });
        await db.SaveChangesAsync();

        // Denetim yazımı erişilemeyen veritabanında sessizce atlanır (Audit.WriteAsync hatayı yutar).
        var sql = new Sql(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1;Database=x;Username=x"));
        var http = new HttpClient();
        var ctl = new AssetsController(db, sql, new EmployeeDirectoryClient(http, new HttpContextAccessor()), tenant)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return new World(ctl, db, free.Id, assigned.Id, history.Id, maint.Id);
    }

    private static (int? Status, string? Message) Err(IActionResult r) => r is ObjectResult o
        ? (o.StatusCode, o.Value is null ? null : JsonSerializer.SerializeToElement(o.Value).TryGetProperty("message", out var m) ? m.GetString() : null)
        : (null, null);

    [Fact]
    public async Task Duzenleme_alanlari_gunceller()
    {
        var w = await MakeAsync();
        var r = await w.Ctl.Update(w.Free, new UpdateAssetRequest(" DMB-100 ", AssetType.Monitor, " Dell U2723 ", ""), default);
        Assert.IsType<OkObjectResult>(r);
        var a = await w.Db.Assets.SingleAsync(x => x.Id == w.Free);
        Assert.Equal("DMB-100", a.AssetTag);
        Assert.Equal(AssetType.Monitor, a.Type);
        Assert.Equal("Dell U2723", a.Model);
        Assert.Null(a.SerialNumber);
    }

    [Fact]
    public async Task Ayni_etiket_409()
    {
        var w = await MakeAsync();
        var r = await w.Ctl.Update(w.Free, new UpdateAssetRequest("DMB-002", AssetType.Laptop, null, null), default);
        Assert.Equal(409, Err(r).Status);
        // Kendi etiketini korumak çakışma değildir.
        Assert.IsType<OkObjectResult>(await w.Ctl.Update(w.Free, new UpdateAssetRequest("DMB-001", AssetType.Phone, null, null), default));
    }

    [Theory]
    [InlineData(AssetStatus.Retired)]
    [InlineData(AssetStatus.Lost)]
    [InlineData(AssetStatus.Maintenance)]
    public async Task Bostaki_demirbasin_durumu_degisir_ve_geri_alinir(AssetStatus to)
    {
        var w = await MakeAsync();
        Assert.IsType<OkObjectResult>(await w.Ctl.SetStatus(w.Free, new SetAssetStatusRequest(to, "test"), default));
        Assert.Equal(to, (await w.Db.Assets.SingleAsync(x => x.Id == w.Free)).Status);
        Assert.IsType<OkObjectResult>(await w.Ctl.SetStatus(w.Free, new SetAssetStatusRequest(AssetStatus.Available, null), default));
        Assert.Equal(AssetStatus.Available, (await w.Db.Assets.SingleAsync(x => x.Id == w.Free)).Status);
    }

    [Fact]
    public async Task Zimmetliyken_durum_degismez_409()
    {
        var w = await MakeAsync();
        var (status, message) = Err(await w.Ctl.SetStatus(w.Assigned, new SetAssetStatusRequest(AssetStatus.Retired, null), default));
        Assert.Equal(409, status);
        Assert.Contains("iade", message);
        Assert.Equal(400, Err(await w.Ctl.SetStatus(w.Free, new SetAssetStatusRequest(AssetStatus.Assigned, null), default)).Status);
    }

    [Fact]
    public async Task Gecmisi_olmayan_silinir_olan_409()
    {
        var w = await MakeAsync();
        Assert.IsType<NoContentResult>(await w.Ctl.Delete(w.Free, default));
        Assert.False(await w.Db.Assets.AnyAsync(x => x.Id == w.Free));

        foreach (var id in new[] { w.Assigned, w.WithHistory, w.WithMaintenance })
        {
            var (status, message) = Err(await w.Ctl.Delete(id, default));
            Assert.Equal(409, status);
            Assert.Contains("hurdaya", message);
        }
        Assert.IsType<NotFoundResult>(await w.Ctl.Delete(Guid.NewGuid(), default));
    }
}
