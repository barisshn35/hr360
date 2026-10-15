using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GovernanceService.Controllers;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Calendar;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Governance.Tests;

public class RuleEngineTests
{
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;
    private static RuleCondition C(string field, string op, string value) => new() { Field = field, Op = op, Value = value };

    [Theory]
    [InlineData("eq", "Annual", true)]
    [InlineData("eq", "annual", true)]       // büyük/küçük harf duyarsız
    [InlineData("neq", "Sick", true)]
    [InlineData("contains", "nnu", true)]
    [InlineData("eq", "Sick", false)]
    public void Metin_kosullari(string op, string value, bool expected) =>
        Assert.Equal(expected, Dispatcher.Evaluate(new[] { C("Type", op, value) }, P("""{"Type":"Annual"}""")).Item1);

    [Theory]
    [InlineData("gt", "4", true)]
    [InlineData("gte", "5", true)]
    [InlineData("lt", "5", false)]
    [InlineData("lte", "5", true)]
    [InlineData("eq", "5.0", true)]          // sayısal karşılaştırma
    public void Sayisal_kosullar(string op, string value, bool expected) =>
        Assert.Equal(expected, Dispatcher.Evaluate(new[] { C("Days", op, value) }, P("""{"Days":5}""")).Item1);

    [Fact]
    public void Ic_ice_alan_ve_tum_kosullar_birlikte()
    {
        var p = P("""{"Employee":{"Department":"Satış"},"Days":12}""");
        Assert.True(Dispatcher.Evaluate(new[] { C("employee.department", "eq", "satış"), C("Days", "gt", "10") }, p).Item1);
        var (ok, why) = Dispatcher.Evaluate(new[] { C("employee.department", "eq", "satış"), C("Days", "gt", "20") }, p);
        Assert.False(ok);
        Assert.Contains("Days", why);
    }

    [Fact]
    public void Eksik_alan_yalnizca_neq_ile_saglanir()
    {
        Assert.False(Dispatcher.Evaluate(new[] { C("Yok", "eq", "x") }, P("{}")).Item1);
        Assert.True(Dispatcher.Evaluate(new[] { C("Yok", "neq", "x") }, P("{}")).Item1);
    }

    [Fact]
    public void Sablon_yer_tutuculari()
    {
        var text = Dispatcher.Render("{{ozet}} — {{Days}} gün, {{Bilinmeyen}}", P("""{"Days":3}"""), "İzin onaylandı");
        Assert.Equal("İzin onaylandı — 3 gün, {{Bilinmeyen}}", text);
    }

    [Fact]
    public void Olay_aciklamasi()
    {
        Assert.Equal("Ali Veli işe alındı", EventHub.Describe("employee.hired", P("""{"FirstName":"Ali","LastName":"Veli"}""")));
        Assert.Equal("Ayşe onaya gönderdi: İzin", EventHub.Describe("workflow.submitted", P("""{"RequesterName":"Ayşe","Subject":"İzin"}""")));
        Assert.Equal("bilinmeyen.olay", EventHub.Describe("bilinmeyen.olay", null));
    }
}

public class TextTests
{
    [Fact]
    public void Turkce_normalizasyon() => Assert.Equal("isci sigortasi ozeti cagri", NlReport.Norm("İŞÇİ Sigortası Özeti Çağrı"));

    [Fact]
    public void Ortam_degiskeni_bos_ise_varsayilan()
    {
        Environment.SetEnvironmentVariable("HR360_TEST_EMPTY", "  ");
        Assert.Equal("varsayılan", EnvVar.Or("HR360_TEST_EMPTY", "varsayılan"));
        Environment.SetEnvironmentVariable("HR360_TEST_EMPTY", "değer");
        Assert.Equal("değer", EnvVar.Or("HR360_TEST_EMPTY", "varsayılan"));
    }
}

// TENANT_SECRET_KEY süreç genelinde değiştirildiği için bu sınıflar paralel koşmaz (rastgele AES hatası).
[Collection("TenantSecretKey")]
public class SecurityTests
{
    private static string Sign(string secret, string ts, string body) =>
        "v0=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{ts}:{body}"))).ToLowerInvariant();

    [Fact]
    public void Slack_imzasi_dogrulanir()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string body = "user_id=U1&text=bakiye";
        Assert.True(SlackApi.VerifySignature("gizli", ts, Sign("gizli", ts, body), body));
        Assert.False(SlackApi.VerifySignature("gizli", ts, Sign("baska", ts, body), body));
        Assert.False(SlackApi.VerifySignature("gizli", ts, Sign("gizli", ts, body), body + "&x=1"));   // gövde değişmiş
    }

    [Fact]
    public void Slack_eski_zaman_damgasi_reddedilir()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        Assert.False(SlackApi.VerifySignature("gizli", ts, Sign("gizli", ts, "a=1"), "a=1"));
    }

    [Fact]
    public void Pkce_S256_baska_uygulamayla_ayni_sonuc() =>
        // Beklenen değer Python hashlib + base64.urlsafe_b64encode ile bağımsız hesaplandı.
        Assert.Equal("u1QvlTzRt344kjsG50ITuOJIjS-jKuVeqQ8i9Xc2Nuk", Pkce.Challenge("dBjftJeZ4CVP-mJ92K5RbV1NZp-IuolDyXzGZu8pAEk"));

    [Fact]
    public void Pkce_dogrulayici_ve_state_benzersiz_ve_url_guvenli()
    {
        var v = Pkce.NewVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.DoesNotContain('+', v + Pkce.NewState());
        Assert.NotEqual(Pkce.NewState(), Pkce.NewState());
    }

    [Fact]
    public void Sir_kutusu_sifreler_ve_kurcalamayi_yakalar()
    {
        Environment.SetEnvironmentVariable("TENANT_SECRET_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var sealedValue = SecretBox.Protect("xoxb-gizli-jeton")!;
        Assert.DoesNotContain("xoxb", sealedValue);
        Assert.Equal("xoxb-gizli-jeton", SecretBox.Unprotect(sealedValue));
        Assert.NotEqual(sealedValue, SecretBox.Protect("xoxb-gizli-jeton"));     // rastgele nonce
        var bytes = Convert.FromBase64String(sealedValue);
        bytes[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => SecretBox.Unprotect(Convert.ToBase64String(bytes)));
        Assert.Null(SecretBox.Protect(null));
        Assert.Null(SecretBox.Unprotect(""));
    }
}

public class ChatFormatTests
{
    private static readonly PendingApproval P = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "LeaveRequest", "3 günlük izin <test>", "Ayşe Yılmaz", new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), "5 Eki – 7 Eki 2026 · 3 gün");

    private static IEnumerable<JsonNode> Buttons(JsonArray blocks) =>
        blocks.Where(b => (string?)b!["type"] == "actions").SelectMany(b => b!["elements"]!.AsArray()).Select(e => e!);

    [Fact]
    public void Slack_onay_mesaji_dugmeleri_ve_degeri()
    {
        var blocks = ChatFormat.SlackApproval(P, null);
        var ids = Buttons(blocks).Select(b => (string?)b["action_id"]).ToList();
        Assert.Equal(new[] { ChatFormat.ApproveAction, ChatFormat.RejectAction, "hr360_open" }, ids);
        Assert.Equal($"{P.WorkflowId}|{P.StepId}", (string?)Buttons(blocks).First()["value"]);
        Assert.Null(Buttons(blocks).ElementAt(1)["confirm"]);                             // Reddet gerekçe penceresi açar (ayrı onay yok)
        var section = (string)blocks[0]!["text"]!["text"]!;
        Assert.Contains("&lt;test&gt;", section);                                            // mrkdwn kaçışı
        Assert.StartsWith("*İzin talebi onayınızı bekliyor*", section);
    }

    [Fact]
    public void Karar_sonrasi_dugmeler_kalkar_durum_eklenir()
    {
        var blocks = ChatFormat.SlackApproval(P, "✅ Onayladınız");
        Assert.DoesNotContain(Buttons(blocks), b => (string?)b["action_id"] == ChatFormat.ApproveAction);
        Assert.Contains(blocks, b => (string?)b!["type"] == "context" && b!["elements"]![0]!["text"]!.ToString().Contains("Onayladınız"));
        var card = ChatFormat.TeamsApprovalCard(P, "⛔ Reddettiniz");
        Assert.DoesNotContain("Action.Submit", card.ToJsonString());
        Assert.Contains("Attention", card.ToJsonString());
    }

    [Fact]
    public void Teams_kart_karar_verisi()
    {
        var card = ChatFormat.TeamsApprovalCard(P, null);
        var submit = card["actions"]!.AsArray().Where(a => (string?)a!["type"] == "Action.Submit").Select(a => a!["data"]!).ToList();
        Assert.Equal(2, submit.Count);
        Assert.Equal("approve", (string?)submit[0]["decision"]);
        Assert.Equal(P.StepId.ToString(), (string?)submit[1]["step"]);
        Assert.Equal("1.4", (string?)card["version"]);
    }

    [Fact]
    public void Teams_metninde_kalin_bicim_donusur() =>
        Assert.Contains("**12**", ChatFormat.TeamsTextCard("Kalan: *12* gün", null).ToJsonString());

    [Fact]
    public void Teams_webhook_mesaji_adaptive_card() =>
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)ChatFormat.TeamsWebhookMessage("x", "y")["attachments"]![0]!["contentType"]);
}

public class CalendarTests
{
    private static CalendarService.PersonAvailability Person(params (DateTime S, DateTime E)[] busy) =>
        new(Guid.NewGuid(), "x", true, busy.Select(b => new CalendarService.Busy(b.S, b.E, "takvim")).ToList());

    [Fact]
    public void Oneriler_is_saatinde_ve_dolu_araliklari_atlar()
    {
        var monday = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);              // Pazartesi
        // 09:00–10:00 TR (06–07Z) dolu
        var s = CalendarService.Suggest(new() { Person((monday.AddHours(6), monday.AddHours(7))) }, monday, monday.AddDays(1), 60, 3);
        Assert.Equal(new[] { monday.AddHours(7), monday.AddHours(7.5), monday.AddHours(8) }, s);
    }

    [Fact]
    public void Hafta_sonu_ve_mesai_disi_onerilmez()
    {
        var saturday = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Empty(CalendarService.Suggest(new() { Person() }, saturday, saturday.AddDays(2), 30));
        var monday = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var s = CalendarService.Suggest(new() { Person() }, monday, monday.AddDays(1), 30, 100);
        Assert.All(s, t => Assert.InRange(t.AddHours(3).Hour, 9, 17));
        Assert.Equal(monday.AddHours(15).AddMinutes(-30), s[^1]);                        // son aralık 17:30 TR
    }
}

public class TeamsPackageTests
{
    [Theory]
    [InlineData(192)]
    [InlineData(32)]
    public void Png_simgesi_gecerli(int size)
    {
        var png = Png.Icon(size, color: size > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        Assert.Equal(size, w);
    }
}

public class AppCacheTests
{
    private static AppCache Make(string? url)
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(url is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["REDIS_URL"] = url })
            .Build();
        return new AppCache(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<AppCache>.Instance);
    }

    [Fact]
    public async Task Redis_tanimli_degilse_her_cagri_kaynaktan_okunur()
    {
        using var cache = Make(null);
        var calls = 0;
        for (var i = 0; i < 3; i++)
            Assert.Equal(42, await cache.GetOrSetAsync("t", "acme", "x", TimeSpan.FromMinutes(1), _ => { calls++; return Task.FromResult(42); }, default));
        Assert.False(cache.Enabled);
        Assert.Equal(3, calls);
        Assert.Null(await cache.CountInWindowAsync("rate", "k", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Redis_erisilemezse_sonuc_doner_ve_sonraki_cagrilar_beklemez()
    {
        using var cache = Make("127.0.0.1:1");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("ok", await cache.GetOrSetAsync("t", "acme", "x", TimeSpan.FromMinutes(1), _ => Task.FromResult("ok"), default));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"ilk çağrı {sw.Elapsed}");
        sw.Restart();
        for (var i = 0; i < 20; i++)
            await cache.GetOrSetAsync("t", "acme", "x", TimeSpan.FromMinutes(1), _ => Task.FromResult("ok"), default);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"devre kesici açıkken 20 çağrı {sw.Elapsed}");
        Assert.Equal("0", await cache.VersionAsync("presence", "acme"));
    }

    [Fact]
    public async Task Kayit_tipleri_json_ile_bozulmadan_doner()
    {
        using var cache = Make(null);
        var p = new Person(Guid.NewGuid(), "Ayşe Yılmaz", "a@x.com", "Geliştirici", Guid.NewGuid(), "Ar-Ge", new DateOnly(2020, 1, 2), "Active", "u1", null);
        var round = System.Text.Json.JsonSerializer.Deserialize<List<Person>>(System.Text.Json.JsonSerializer.Serialize(new List<Person> { p }))!;
        Assert.Equal(p, round[0]);
        Assert.Equal(p, (await cache.GetOrSetAsync("people", "acme", "active", TimeSpan.FromSeconds(30), _ => Task.FromResult(new List<Person> { p }), default))[0]);
    }
}

public class KvkkOpsTests
{
    private static Dictionary<string, KvkkOpsController.AnswerInput> A(params (string Code, string Answer)[] xs) =>
        xs.ToDictionary(x => x.Code, x => new KvkkOpsController.AnswerInput(x.Answer, null));

    [Fact]
    public void Ozel_nitelikli_ve_yurt_disi_yuksek_risk() =>
        Assert.Equal("High", KvkkOpsController.ComputeRisk(A(("special", "yes"), ("abroad", "yes"))));

    [Fact]
    public void Guvenli_yanitlar_dusuk_risk()
    {
        var safe = KvkkOpsController.Questions.ToDictionary(q => q.Code, q => new KvkkOpsController.AnswerInput(q.RiskyAnswer == "yes" ? "no" : "yes", null));
        Assert.Equal("Low", KvkkOpsController.ComputeRisk(safe));
    }

    [Fact]
    public void Izleme_ve_eksik_onlem_orta_risk() =>
        Assert.Equal("Medium", KvkkOpsController.ComputeRisk(A(("monitoring", "yes"), ("minimal", "no"))));

    [Fact]
    public void Siem_takma_ad_sabit_ve_geri_cevrilemez()
    {
        var a = SiemExporter.Pseudonym("user-123");
        Assert.Equal(a, SiemExporter.Pseudonym("user-123"));
        Assert.NotEqual(a, SiemExporter.Pseudonym("user-124"));
        Assert.StartsWith("p_", a);
        Assert.DoesNotContain("user", a);
        Assert.Equal("-", SiemExporter.Pseudonym(null));
    }

    [Theory]
    [InlineData("203.0.113.57", "203.0.113.0/24")]
    [InlineData("bozuk", "-")]
    [InlineData(null, "-")]
    public void Siem_ip_maskeleme(string? ip, string expected) => Assert.Equal(expected, SiemExporter.MaskIp(ip));

    [Fact]
    public void Siem_satiri_rfc5424_ve_icerik_yok()
    {
        var line = SiemExporter.Format(new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc), "demo", "leave-service", "LeaveRequest",
            "8c7dd608-46e2-4bee-900f-6a175b21d3b2", "Modified", "kc-user-1", "10.1.2.3", 42, "abc");
        Assert.StartsWith("<110>1 2026-10-06T08:00:00.000000Z ", line);
        Assert.Contains("[hr360@32473 tenant=\"demo\"", line);
        Assert.Contains("net=\"10.1.2.0/24\"", line);
        Assert.Contains("seq=\"42\"", line);
        Assert.DoesNotContain("kc-user-1", line);
        Assert.DoesNotContain("8c7dd608", line);
    }
}
