using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OnboardingService.Controllers;
using OnboardingService.Data;
using OnboardingService.Infrastructure;
using OnboardingService.Models;
using OnboardingService.Services;
using OnboardingService.Tenancy;
using Xunit;

namespace Onboarding.Tests;

/// <summary>Plan ve demirbaş listelerinde sunucu tarafı sayfalama (geriye uyumlu: page yoksa düz dizi).</summary>
public class PagingTests
{
    private static readonly Guid Ayse = Guid.NewGuid();

    private static OnboardingDbContext NewDb(TenantContext tenant) =>
        new(new DbContextOptionsBuilder<OnboardingDbContext>().UseInMemoryDatabase("paging-" + Guid.NewGuid()).Options, tenant);

    private static ControllerContext Ctx() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "hr-admin") }, "test")),
        },
    };

    private static Sql NoSql() => new(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1;Database=x;Username=x"));

    private static async Task<(AssetsController Ctl, Guid Assigned)> AssetsAsync()
    {
        var tenant = new TenantContext { TenantSlug = "demo" };
        var db = NewDb(tenant);
        // 25 demirbaş; DMB-002 Ayşe'de zimmetli, DMB-003'ün ataması iade edilmiş.
        var assets = Enumerable.Range(1, 25).Select(i => new Asset
        {
            AssetTag = $"DMB-{i:000}",
            Type = i % 2 == 0 ? AssetType.Phone : AssetType.Laptop,
            Model = i == 7 ? "Çalışma İstasyonu" : $"Model {i}",
            SerialNumber = i == 9 ? "SN_50%" : $"SN{i}",
            Status = i == 2 ? AssetStatus.Assigned : AssetStatus.Available,
        }).ToList();
        db.Assets.AddRange(assets);
        db.AssetAssignments.Add(new AssetAssignment { AssetId = assets[1].Id, EmployeeId = Ayse, AssignedOn = new DateOnly(2026, 9, 1) });
        db.AssetAssignments.Add(new AssetAssignment { AssetId = assets[2].Id, EmployeeId = Ayse, AssignedOn = new DateOnly(2026, 1, 1), ReturnedOn = new DateOnly(2026, 6, 1) });
        await db.SaveChangesAsync();
        var ctl = new AssetsController(db, NoSql(), new EmployeeDirectoryClient(new HttpClient(), new HttpContextAccessor()), tenant)
        {
            ControllerContext = Ctx(),
        };
        return (ctl, assets[1].Id);
    }

    private static async Task<OnboardingPlansController> PlansAsync()
    {
        var tenant = new TenantContext { TenantSlug = "demo" };
        var db = NewDb(tenant);
        var start = DateTimeOffset.UtcNow.AddDays(-30);
        for (var i = 0; i < 15; i++)
        {
            var plan = new OnboardingPlan
            {
                EmployeeId = i == 4 ? Ayse : Guid.NewGuid(),
                StartDate = new DateOnly(2026, 10, 1).AddDays(i),
                TemplateName = i == 3 ? "Yazılım Geliştirici" : "Standart",
                Status = i < 10 ? PlanStatus.InProgress : PlanStatus.Completed,
                CreatedAt = start.AddDays(i),
            };
            // i. planın i görevinden yarısı tamam.
            for (var t = 0; t < i; t++)
                plan.Tasks.Add(new OnboardingTask { Title = $"G{t}", Category = TaskCategory.HR, Status = t < i / 2 ? OnboardingTaskStatus.Done : OnboardingTaskStatus.Pending });
            db.Plans.Add(plan);
        }
        await db.SaveChangesAsync();
        return new OnboardingPlansController(db, new EmployeeDirectoryClient(new HttpClient(), new HttpContextAccessor()), NoSql(), tenant)
        {
            ControllerContext = Ctx(),
        };
    }

    // Program.cs ile aynı JSON ayarları (döngü yok sayılır, enum metin).
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    private static JsonElement Json(IActionResult r) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value, Web);

    private static string Total(ControllerBase c) => c.Response.Headers["X-Total-Count"].ToString();

    private static List<string> Tags(JsonElement arr) => arr.EnumerateArray().Select(a => a.GetProperty("assetTag").GetString()!).ToList();

    [Fact]
    public async Task Demirbas_page_yoksa_duz_dizi_ve_toplam_basligi()
    {
        var (ctl, assigned) = await AssetsAsync();
        var json = Json(await ctl.GetAll(null, null, default));
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(25, json.GetArrayLength());
        Assert.Equal("25", Total(ctl));
        Assert.Equal(Tags(json).OrderBy(t => t, StringComparer.Ordinal), Tags(json));
        // Açık atama düz alan olarak gelir; iade edilmiş atama gelmez.
        var a2 = json.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == assigned);
        Assert.Equal(Ayse, a2.GetProperty("assignedEmployeeId").GetGuid());
        var a3 = json.EnumerateArray().Single(a => a.GetProperty("assetTag").GetString() == "DMB-003");
        Assert.Equal(JsonValueKind.Null, a3.GetProperty("assignedEmployeeId").ValueKind);
    }

    [Fact]
    public async Task Demirbas_sayfalari_cakismaz_ve_birlesimi_duz_liste()
    {
        var (ctl, _) = await AssetsAsync();
        var all = Tags(Json(await ctl.GetAll(null, null, default)));
        var seen = new List<string>();
        for (var p = 1; p <= 3; p++)
        {
            var json = Json(await ctl.GetAll(null, null, default, page: p, pageSize: 10));
            Assert.Equal(25, json.GetProperty("total").GetInt32());
            Assert.Equal(p, json.GetProperty("page").GetInt32());
            Assert.Equal(10, json.GetProperty("pageSize").GetInt32());
            seen.AddRange(Tags(json.GetProperty("items")));
        }
        Assert.Equal(all, seen);
        Assert.Equal("25", Total(ctl));

        // Sayfadaki zimmetli demirbaşın açık ataması yine düz alan olarak gelir.
        var first = Json(await ctl.GetAll(null, null, default, page: 1, pageSize: 2)).GetProperty("items");
        Assert.Equal(Ayse, first[1].GetProperty("assignedEmployeeId").GetGuid());
    }

    [Fact]
    public async Task Demirbas_boyut_sinirlanir_ve_filtre_toplami_dogru()
    {
        var (ctl, _) = await AssetsAsync();
        var big = Json(await ctl.GetAll(null, null, default, page: 0, pageSize: 1000));
        Assert.Equal(1, big.GetProperty("page").GetInt32());
        Assert.Equal(Paging.MaxPageSize, big.GetProperty("pageSize").GetInt32());
        Assert.Equal(25, big.GetProperty("items").GetArrayLength());

        var phones = Json(await ctl.GetAll(null, AssetType.Phone, default, page: 1, pageSize: 5));
        Assert.Equal(12, phones.GetProperty("total").GetInt32());
        Assert.Equal(5, phones.GetProperty("items").GetArrayLength());

        var desc = Json(await ctl.GetAll(null, null, default, page: 1, pageSize: 1, sort: "tag", dir: "desc"));
        Assert.Equal("DMB-025", desc.GetProperty("items")[0].GetProperty("assetTag").GetString());
    }

    [Fact]
    public async Task Demirbas_aramasi_katlamali_ve_kacisli()
    {
        var (ctl, _) = await AssetsAsync();
        // "calisma istasyonu" → "Çalışma İstasyonu" (model).
        Assert.Equal(new[] { "DMB-007" }, Tags(Json(await ctl.GetAll(null, null, default, q: "calisma istasyonu"))));
        // % ve _ joker değil, düz karakter.
        Assert.Equal(new[] { "DMB-009" }, Tags(Json(await ctl.GetAll(null, null, default, q: "_50%"))));
        // Zimmetli kişi (istemci çözer): yalnızca açık ataması olan.
        var byPerson = Json(await ctl.GetAll(null, null, default, page: 1, q: "ayse", qEmployeeIds: Ayse.ToString()));
        Assert.Equal(new[] { "DMB-002" }, Tags(byPerson.GetProperty("items")));
        // Tür adı (istemci çözer).
        Assert.Equal(12, Json(await ctl.GetAll(null, null, default, page: 1, q: "telefon", qTypes: "Phone,xyz")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Plan_page_yoksa_duz_dizi_yeni_once()
    {
        var ctl = await PlansAsync();
        var json = Json(await ctl.GetAll(null, null, default));
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(15, json.GetArrayLength());
        Assert.Equal("15", Total(ctl));
        var created = json.EnumerateArray().Select(p => p.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(created.OrderByDescending(c => c), created);
        // Görevler eskisi gibi plana gömülü.
        Assert.Equal(14, json[0].GetProperty("tasks").GetArrayLength());
    }

    [Fact]
    public async Task Plan_sayfalari_filtre_ve_siralama()
    {
        var ctl = await PlansAsync();
        var all = Json(await ctl.GetAll(null, PlanStatus.InProgress, default)).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(10, all.Count);
        var seen = new List<Guid>();
        for (var p = 1; p <= 2; p++)
        {
            var json = Json(await ctl.GetAll(null, PlanStatus.InProgress, default, page: p, pageSize: 6));
            Assert.Equal(10, json.GetProperty("total").GetInt32());
            seen.AddRange(json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        }
        Assert.Equal(all, seen);

        var byStart = Json(await ctl.GetAll(null, null, default, page: 1, pageSize: 1, sort: "startDate", dir: "asc"));
        Assert.Equal("2026-10-01", byStart.GetProperty("items")[0].GetProperty("startDate").GetString());

        // İlerleme: tamamlanan görev oranı artan (görevsiz plan 0 sayılır).
        var byProgress = Json(await ctl.GetAll(null, null, default, page: 1, pageSize: 200, sort: "progress", dir: "asc")).GetProperty("items");
        var ratios = byProgress.EnumerateArray().Select(p =>
        {
            var tasks = p.GetProperty("tasks").EnumerateArray().ToList();
            return tasks.Count == 0 ? 0.0 : (double)tasks.Count(t => t.GetProperty("status").GetString() == "Done") / tasks.Count;
        }).ToList();
        Assert.Equal(15, ratios.Count);
        Assert.Equal(ratios.OrderBy(r => r), ratios);

        var byStatus = Json(await ctl.GetAll(null, null, default, page: 1, pageSize: 1, sort: "status", dir: "desc"));
        Assert.Equal("Completed", byStatus.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Plan_aramasi_sablon_adi_ve_calisan_kimligi()
    {
        var ctl = await PlansAsync();
        var bySablon = Json(await ctl.GetAll(null, null, default, page: 1, q: "yazilim"));
        Assert.Equal(1, bySablon.GetProperty("total").GetInt32());
        Assert.Equal("Yazılım Geliştirici", bySablon.GetProperty("items")[0].GetProperty("templateName").GetString());

        var byPerson = Json(await ctl.GetAll(null, null, default, page: 1, q: "ayşe", qEmployeeIds: $"{Ayse},bozuk"));
        Assert.Equal(1, byPerson.GetProperty("total").GetInt32());
        Assert.Equal(Ayse, byPerson.GetProperty("items")[0].GetProperty("employeeId").GetGuid());
    }

    [Theory]
    [InlineData("Ayşe", "ayse")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("IŞIK Çağlar", "isik caglar")]
    public void Fold_matches_sql_hr360_fold(string input, string expected) =>
        Assert.Equal(expected, Paging.Fold(input));
}
