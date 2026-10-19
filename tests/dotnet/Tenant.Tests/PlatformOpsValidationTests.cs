using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using TenantService.Controllers;
using Xunit;

namespace Tenant.Tests;

/// <summary>Platform › Bakım iş doğrulaması; ozellikle anlik geri donus (pitr-*) turleri.</summary>
public class PlatformOpsValidationTests
{
    // 2026-10-19 12:00 UTC = 15:00 Istanbul
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement Snap(string? oldest = "2026-10-18T03:01:40Z") => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        backups = new[] { new { name = "hr360-20261019.tar.gz" } },
        tags = new[] { "v1.2.0" },
        pitr = new { state = "on", oldestRecoverable = oldest },
    })).RootElement;

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private static (object? Args, string? Error) Restore(object target, string? confirm = PlatformOpsController.PitrConfirm, JsonElement? snap = null)
        => PlatformOpsController.ValidateArgs("pitr-restore", Args(new { target }), confirm, snap ?? Snap(), Now);

    [Fact]
    public void Yeni_turler_izinli_ve_geri_donus_yikici()
    {
        foreach (var k in new[] { "pitr-enable", "pitr-disable", "pitr-restore", "pitr-cleanup" })
            Assert.Contains(k, PlatformOpsController.Kinds);
        var destructive = (string[])typeof(PlatformOpsController).GetField("Destructive", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Contains("pitr-restore", destructive);
    }

    [Fact]
    public void Yalnizca_platform_yoneticisi()
    {
        var a = typeof(PlatformOpsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("RequirePlatformAdmin", a?.Policy);
    }

    [Fact]
    public void Gecerli_geri_donus_kabul_edilir()
    {
        var (args, error) = Restore("2026-10-19 14:05");
        Assert.Null(error);
        Assert.Equal("""{"target":"2026-10-19 14:05"}""", JsonSerializer.Serialize(args));
    }

    [Theory]
    [InlineData("2026-10-19")]
    [InlineData("2026-10-19T14:05")]
    [InlineData("2026-10-19 14:05:00")]
    [InlineData("2026-10-19 14:05\n")]
    [InlineData(" 2026-10-19 14:05")]
    [InlineData("2026-10-19 14:05; reboot")]
    [InlineData("2026-10-19 14:05 --yes")]
    [InlineData("$(id)")]
    [InlineData("2026-13-01 10:00")]
    [InlineData("2026-02-30 10:00")]
    [InlineData("2026-10-19 24:00")]
    [InlineData("٢٠٢٦-10-19 14:05")]
    public void Bicim_disi_an_reddedilir(string target)
        => Assert.Equal("Geri dönülecek an YYYY-AA-GG SS:DD biçiminde olmalı", Restore(target).Error);

    [Fact]
    public void Metin_olmayan_an_reddedilir()
    {
        Assert.NotNull(Restore(202610191405).Error);
        Assert.NotNull(PlatformOpsController.ValidateArgs("pitr-restore", null, PlatformOpsController.PitrConfirm, Snap(), Now).Error);
    }

    [Fact]
    public void Gelecek_ve_su_an_reddedilir()
    {
        Assert.Equal("Geri dönülecek an geçmişte olmalı", Restore("2026-10-19 15:00").Error); // = simdi (Istanbul)
        Assert.Equal("Geri dönülecek an geçmişte olmalı", Restore("2026-10-20 09:00").Error);
        Assert.Null(Restore("2026-10-19 14:59").Error);
    }

    [Fact]
    public void Aralik_disi_ya_da_temel_yedeksiz_reddedilir()
    {
        // en eski temel yedek 03:01:40 UTC = 06:01:40 Istanbul bitti
        Assert.Equal("Bu an geri dönülebilecek aralığın dışında", Restore("2026-10-18 06:01").Error);
        Assert.Null(Restore("2026-10-18 06:02").Error);
        Assert.Equal("Geri dönülebilecek an yok; önce anlık geri dönüşü açın", Restore("2026-10-19 14:05", snap: Snap(null)).Error);
        var old = JsonDocument.Parse("""{"backups":[],"tags":[]}""").RootElement; // eski calistirici: pitr yok
        Assert.NotNull(Restore("2026-10-19 14:05", snap: old).Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GERİ YÜKLE")]
    [InlineData("geri dön")]
    public void Yazili_onay_gerekir(string? confirm)
        => Assert.Equal($"Onay için \"{PlatformOpsController.PitrConfirm}\" yazın", Restore("2026-10-19 14:05", confirm).Error);

    [Fact]
    public void Kapatma_yalnizca_gercek_true_ile_arsivi_siler()
    {
        string Purge(object args) => JsonSerializer.Serialize(PlatformOpsController.ValidateArgs("pitr-disable", Args(args), null, Snap(), Now).Args);
        Assert.Equal("""{"purge":true}""", Purge(new { purge = true }));
        Assert.Equal("""{"purge":false}""", Purge(new { purge = "true" }));
        Assert.Equal("""{"purge":false}""", Purge(new { }));
    }

    [Fact]
    public void Diger_pitr_isleri_bagimsiz_degisken_tasimaz()
    {
        foreach (var k in new[] { "pitr-enable", "pitr-cleanup" })
        {
            var (args, error) = PlatformOpsController.ValidateArgs(k, Args(new { cmd = "; rm -rf /" }), null, Snap(), Now);
            Assert.Null(error);
            Assert.Equal("{}", JsonSerializer.Serialize(args));
        }
    }

    [Fact]
    public void Mevcut_geri_yukleme_dogrulamasi_degismedi()
    {
        var snap = Snap();
        Assert.Equal("Yedek bulunamadı", PlatformOpsController.ValidateArgs("restore", Args(new { archive = "../../etc/passwd" }), PlatformOpsController.RestoreConfirm, snap, Now).Error);
        Assert.NotNull(PlatformOpsController.ValidateArgs("restore", Args(new { archive = "hr360-20261019.tar.gz" }), null, snap, Now).Error);
        Assert.Null(PlatformOpsController.ValidateArgs("restore", Args(new { archive = "hr360-20261019.tar.gz" }), PlatformOpsController.RestoreConfirm, snap, Now).Error);
        Assert.NotNull(PlatformOpsController.ValidateArgs("update", Args(new { @ref = "v9.9.9" }), null, snap, Now).Error);
        Assert.NotNull(PlatformOpsController.ValidateArgs("schedule", Args(new { at = "25:00", keep = 14 }), null, snap, Now).Error);
    }
}
