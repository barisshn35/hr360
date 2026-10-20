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

    // --- Modüller ve şirket kipi ---

    private static JsonElement ModSnap(string[]? enabled, int? tenants = 1) => JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["backups"] = Array.Empty<object>(),
        ["tags"] = Array.Empty<string>(),
        ["modules"] = enabled is null ? null : new { enabled, all = PlatformOpsController.Modules },
        ["tenancy"] = new { mode = "multi", tenants },
    }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value))).RootElement;

    private static (object? Args, string? Error) SetModules(object modules, string[]? enabled, string? confirm = null)
        => PlatformOpsController.ValidateArgs("modules-set", Args(new { modules }), confirm, ModSnap(enabled), Now);

    [Fact]
    public void Modul_ve_kip_turleri_izinli_ve_yikici()
    {
        var destructive = (string[])typeof(PlatformOpsController).GetField("Destructive", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (var k in new[] { "modules-set", "tenancy-multi", "tenancy-single" })
        {
            Assert.Contains(k, PlatformOpsController.Kinds);
            Assert.Contains(k, destructive);
        }
    }

    [Fact]
    public void Yalnizca_acma_onaysiz_ve_tablo_sirasiyla()
    {
        var (args, error) = SetModules(new[] { "ml", "timeshift" }, ["timeshift"]);
        Assert.Null(error);
        Assert.Equal("""{"modules":["timeshift","ml"]}""", JsonSerializer.Serialize(args));
        Assert.Null(SetModules(PlatformOpsController.Modules, ["ml"]).Error);
        Assert.Null(SetModules(new[] { "ml" }, ["ml"]).Error); // degisiklik yok
    }

    [Fact]
    public void Kapatma_yazili_onay_ister()
    {
        Assert.Equal("Modül kapatmak için \"MODÜLÜ KAPAT\" yazın", SetModules(new[] { "timeshift" }, ["timeshift", "ml"]).Error);
        Assert.NotNull(SetModules(new[] { "timeshift" }, ["timeshift", "ml"], "modülü kapat").Error);
        Assert.NotNull(SetModules(new[] { "timeshift" }, ["timeshift", "ml"], PlatformOpsController.RestoreConfirm).Error);
        Assert.Null(SetModules(new[] { "timeshift" }, ["timeshift", "ml"], PlatformOpsController.ModuleOffConfirm).Error);
        // bos secim = yalnizca cekirdek
        var (args, error) = SetModules(Array.Empty<string>(), ["ml"], PlatformOpsController.ModuleOffConfirm);
        Assert.Null(error);
        Assert.Equal("""{"modules":[]}""", JsonSerializer.Serialize(args));
        Assert.NotNull(SetModules(Array.Empty<string>(), ["ml"]).Error);
        // ozette modul listesi yoksa (eski calistirici) hepsi acik sayilir
        Assert.NotNull(SetModules(new[] { "ml" }, null).Error);
        Assert.Null(SetModules(PlatformOpsController.Modules, null).Error);
    }

    [Fact]
    public void Gecersiz_modul_secimi_reddedilir()
    {
        Assert.Equal("Bilinmeyen modül", SetModules(new[] { "bordro" }, null, PlatformOpsController.ModuleOffConfirm).Error);
        foreach (var bad in new[] { "employee", "Timeshift", "timeshift ", "timeshift,ml", "all", "ml; reboot", "$(id)", "--no-start", "ml\n" })
            Assert.Equal("Bilinmeyen modül", SetModules(new[] { bad }, null, PlatformOpsController.ModuleOffConfirm).Error);
        Assert.Equal("Bilinmeyen modül", SetModules(new object[] { 1 }, null, PlatformOpsController.ModuleOffConfirm).Error);
        Assert.Equal("Modül seçimi yinelenen öğe içeriyor", SetModules(new[] { "ml", "ml" }, null, PlatformOpsController.ModuleOffConfirm).Error);
        Assert.Equal("Modül seçimi liste olmalı", SetModules("timeshift,ml", null, PlatformOpsController.ModuleOffConfirm).Error);
        Assert.Equal("Modül seçimi liste olmalı", PlatformOpsController.ValidateArgs("modules-set", null, PlatformOpsController.ModuleOffConfirm, ModSnap(null), Now).Error);
        Assert.Equal("Modül seçimi liste olmalı", PlatformOpsController.ValidateArgs("modules-set", Args(new { }), PlatformOpsController.ModuleOffConfirm, ModSnap(null), Now).Error);
    }

    [Fact]
    public void Tek_sirket_kipi_yalnizca_tek_sirketle()
    {
        Assert.Null(PlatformOpsController.ValidateArgs("tenancy-single", null, null, ModSnap(null, 1), Now).Error);
        foreach (var n in new int?[] { 0, 2, 7, null })
            Assert.Equal("Tek şirket kipine yalnızca tam olarak bir şirket varken geçilebilir",
                PlatformOpsController.ValidateArgs("tenancy-single", null, null, ModSnap(null, n), Now).Error);
        Assert.NotNull(PlatformOpsController.ValidateArgs("tenancy-single", null, null, Snap(), Now).Error); // ozette kip yok
    }

    [Fact]
    public void Cok_sirketli_kip_yazili_onay_ister()
    {
        Assert.Equal("Onay için \"ÇOK ŞİRKET\" yazın", PlatformOpsController.ValidateArgs("tenancy-multi", null, null, ModSnap(null), Now).Error);
        Assert.NotNull(PlatformOpsController.ValidateArgs("tenancy-multi", null, "çok şirket", ModSnap(null), Now).Error);
        var (args, error) = PlatformOpsController.ValidateArgs("tenancy-multi", Args(new { cmd = "; rm -rf /" }), PlatformOpsController.MultiTenancyConfirm, ModSnap(null), Now);
        Assert.Null(error);
        Assert.Equal("{}", JsonSerializer.Serialize(args));
    }

    private static JsonElement MonSnap(bool enabled) => JsonDocument.Parse(JsonSerializer.Serialize(new { monitoring = new { enabled } })).RootElement;
    private static (object? Args, string? Error) Mon(string kind, object? args, bool enabled = true)
        => PlatformOpsController.ValidateArgs(kind, args is null ? null : Args(args), null, MonSnap(enabled), Now);

    [Fact]
    public void Izleme_turleri_izinli_ve_yeniden_baslatanlar_tek_basina()
    {
        foreach (var k in new[] { "monitoring-enable", "monitoring-disable", "monitoring-alerts", "monitoring-test", "monitoring-tracing" })
            Assert.Contains(k, PlatformOpsController.Kinds);
        var destructive = (string[])typeof(PlatformOpsController).GetField("Destructive", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Contains("monitoring-enable", destructive);
        Assert.Contains("monitoring-tracing", destructive);
        Assert.DoesNotContain("monitoring-alerts", destructive);
    }

    [Fact]
    public void Alarm_e_postasi_dogrulanir_ve_duzenlenir()
    {
        var (args, error) = Mon("monitoring-alerts", new { channel = "email", value = " ops@sirket.com , it@sirket.com.tr " });
        Assert.Null(error);
        Assert.Equal("""{"channel":"email","value":"ops@sirket.com,it@sirket.com.tr"}""", JsonSerializer.Serialize(args));
        Assert.Equal("Geçersiz e-posta adresi: a;id@b.com", Mon("monitoring-alerts", new { channel = "email", value = "a;id@b.com" }).Error);
        Assert.NotNull(Mon("monitoring-alerts", new { channel = "email", value = string.Join(',', Enumerable.Repeat("a@b.co", 11)) }).Error);
        Assert.Equal("Bilinmeyen alarm kanalı", Mon("monitoring-alerts", new { channel = "sms", value = "a@b.co" }).Error);
        // bos deger kanali kapatir
        Assert.Equal("""{"channel":"slack","value":null}""", JsonSerializer.Serialize(Mon("monitoring-alerts", new { channel = "slack", value = "" }).Args));
    }

    [Fact]
    public void Webhook_https_olmali_ve_listede_maskelenir()
    {
        const string url = "https://prod-00.westeurope.logic.azure.com:443/workflows/x/invoke?api-version=1&sig=SECRET";
        Assert.Null(Mon("monitoring-alerts", new { channel = "teams", value = url }).Error);
        foreach (var bad in new[] { "http://hooks.slack.com/services/x", "https://a.com/b c", "https://a.com/#x", "https://a.com/x\nfoo", "javascript:alert(1)", "https://a.com/${HOME}", "https://a.com/a\\b" })
            Assert.NotNull(Mon("monitoring-alerts", new { channel = "slack", value = bad }).Error);
        var masked = PlatformOpsController.RedactArgs("monitoring-alerts", Args(new { channel = "teams", value = url }));
        Assert.Equal("https://prod-00.westeurope.logic.azure.com:443/…", masked.GetProperty("value").GetString());
        Assert.DoesNotContain("SECRET", masked.GetRawText());
        // e-posta ve diger isler olduğu gibi
        var email = Args(new { channel = "email", value = "ops@a.com" });
        Assert.Equal(email.GetRawText(), PlatformOpsController.RedactArgs("monitoring-alerts", email).GetRawText());
    }

    [Fact]
    public void Izleme_kapaliyken_deneme_ve_iz_reddedilir()
    {
        Assert.Equal("Önce izlemeyi açın", Mon("monitoring-test", null, enabled: false).Error);
        Assert.Null(Mon("monitoring-test", null).Error);
        Assert.Equal("Önce izlemeyi açın", Mon("monitoring-tracing", new { on = true, ratio = 0.5 }, enabled: false).Error);
        Assert.Null(Mon("monitoring-tracing", new { on = false }, enabled: false).Error); // kapatmak her zaman olur
    }

    [Fact]
    public void Iz_orani_sinirlanir_ve_metne_cevrilir()
    {
        Assert.Equal("""{"on":true,"ratio":"0.25"}""", JsonSerializer.Serialize(Mon("monitoring-tracing", new { on = true, ratio = 0.25 }).Args));
        Assert.Equal("""{"on":true,"ratio":"1"}""", JsonSerializer.Serialize(Mon("monitoring-tracing", new { on = true, ratio = 1 }).Args));
        foreach (var bad in new object[] { 0, 1.5, -0.1, "0.5", 0.0001 })
            Assert.Equal("Örnekleme oranı 0 ile 1 arasında olmalı", Mon("monitoring-tracing", new { on = true, ratio = bad }).Error);
        Assert.Equal("""{"purge":true}""", JsonSerializer.Serialize(Mon("monitoring-disable", new { purge = true }).Args));
    }
}
