using GovernanceService.Controllers;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>Güvenlik dalgası 2B: toplu görüntüleme dedektörü, filigran/iz kodu, form jetonu, erişim gözden geçirme.</summary>
public class DataProtectionTests
{
    static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    static IEnumerable<AccessEvent> Views(string user, int count, DateTime at, string prefix = "") =>
        Enumerable.Range(0, count).Select(i => new AccessEvent(user, "Ad " + user, prefix + Guid.NewGuid(), at));

    [Fact]
    public void Esigi_asan_kullanici_yakalanir()
    {
        var hits = MassViewDetector.Evaluate(Views("u1", 51, Now.AddMinutes(-3)).Concat(Views("u2", 50, Now.AddMinutes(-3))), 50, 10, Now);
        var h = Assert.Single(hits);
        Assert.Equal("u1", h.UserId);
        Assert.Equal(51, h.Distinct);
        Assert.Equal("Ad u1", h.UserName);
    }

    [Fact]
    public void Ayni_kayit_tekrar_sayilmaz_ve_pencere_disi_sayilmaz()
    {
        var same = Enumerable.Range(0, 200).Select(_ => new AccessEvent("u1", null, "emp-1", Now.AddMinutes(-1)));
        var old = Views("u1", 100, Now.AddMinutes(-11));
        Assert.Empty(MassViewDetector.Evaluate(same.Concat(old), 50, 10, Now));
    }

    [Fact]
    public void Sistem_kullanicisi_ve_ayni_pencerede_uyarilan_tekrar_bildirilmez()
    {
        Assert.Empty(MassViewDetector.Evaluate(Views("system", 80, Now), 50, 10, Now));
        var last = new Dictionary<string, DateTime> { ["u1"] = Now.AddMinutes(-5) };
        Assert.Empty(MassViewDetector.Evaluate(Views("u1", 80, Now.AddMinutes(-1)), 50, 10, Now, last));
        last["u1"] = Now.AddMinutes(-15);
        Assert.Single(MassViewDetector.Evaluate(Views("u1", 80, Now.AddMinutes(-1)), 50, 10, Now, last));
    }

    [Fact]
    public void Kayit_anahtari_guid_ise_tur_bagimsiz()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(MassViewDetector.Key("Employee", id), MassViewDetector.Key("EmployeeProfile", id));
        Assert.Equal("Report:salary", MassViewDetector.Key("Report", "salary"));
    }

    [Theory]
    [InlineData(9, 10, false)]
    [InlineData(10, 1, true)]
    [InlineData(1000, 60, true)]
    [InlineData(50, 61, false)]
    public void Ayar_sinirlari(int threshold, int window, bool ok) =>
        Assert.Equal(ok, SecuritySettings.Validate(threshold, window) is null);

    [Fact]
    public void Filigran_metni_ad_yerel_saat_ve_kod()
    {
        var t = Watermark.Text("Ayşe Yılmaz", new DateTimeOffset(2026, 10, 6, 11, 32, 0, TimeSpan.Zero), "K7P2-MX9Q");
        Assert.Equal("Ayşe Yılmaz · 06.10.2026 14:32 · K7P2-MX9Q", t);
        Assert.StartsWith("? · ", Watermark.Text(" ", DateTimeOffset.UtcNow, "AAAA-BBBB"));
    }

    [Fact]
    public void Iz_kodu_bicimi_ve_normallestirme()
    {
        var c = Watermark.NewCode();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", c);
        Assert.Equal(c, Watermark.NormalizeCode(c.ToLowerInvariant().Replace("-", " ")));
        Assert.Null(Watermark.NormalizeCode("ABCD-EFG"));
        Assert.Null(Watermark.NormalizeCode("ABCD-EFG0")); // 0 alfabede yok
        Assert.NotEqual(Watermark.NewCode(), Watermark.NewCode());
    }

    [Fact]
    public void Form_jetonu_yas_kapsam_imza_ve_tek_kullanim()
    {
        var g = new FormGuard(new byte[32]);
        var t0 = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var tok = g.Issue("ethics:demo", t0);
        Assert.Equal(FormTokenStatus.TooFast, g.Verify(tok, "ethics:demo", t0.AddSeconds(1)));
        Assert.Equal(FormTokenStatus.Invalid, g.Verify(tok, "ethics:baska", t0.AddSeconds(5)));
        Assert.Equal(FormTokenStatus.Expired, g.Verify(tok, "ethics:demo", t0.AddHours(3)));
        Assert.Equal(FormTokenStatus.Ok, g.Verify(tok, "ethics:demo", t0.AddSeconds(5)));
        Assert.Equal(FormTokenStatus.Replayed, g.Verify(tok, "ethics:demo", t0.AddSeconds(6)));
        Assert.Equal(FormTokenStatus.Missing, g.Verify(null, "ethics:demo"));
        var parts = g.Issue("ethics:demo", t0).Split('.');
        var forged = $"{t0.AddSeconds(-60).ToUnixTimeMilliseconds()}.{parts[1]}.{parts[2]}";
        Assert.Equal(FormTokenStatus.Invalid, g.Verify(forged, "ethics:demo", t0.AddSeconds(5)));
        Assert.Equal(FormTokenStatus.Invalid, new FormGuard(Enumerable.Repeat((byte)1, 32).ToArray()).Verify(g.Issue("x", t0), "x", t0.AddSeconds(5)));
        Assert.Equal(FormTokenStatus.Invalid, g.Verify("a.b.c", "x"));
    }

    [Fact]
    public void Erisim_gozden_gecirme_yalnizca_yetkili_hesaplar_ve_gozden_geciren()
    {
        var rows = new List<AccessReviewController.MemberRow>
        {
            new(Guid.NewGuid(), "k1", true, new() { "employee" }, new()),
            new(Guid.NewGuid(), "k2", true, new() { "employee", "manager" }, new()),
            new(Guid.NewGuid(), "k3", true, new() { "employee" }, new() { "compensation:view" }),
            new(Guid.NewGuid(), null, false, new() { "hr-admin" }, new()),
        };
        var r = AccessReviewController.Reviewable(rows);
        Assert.Equal(new[] { "k2", "k3" }, r.Select(x => x.KeycloakUserId));

        var head = Guid.NewGuid();
        Person P(Guid id, Guid? headId) => new(id, "X", null, null, null, null, new DateOnly(2020, 1, 1), "Active", null, headId);
        Assert.Equal(head, AccessReviewController.ReviewerFor(P(Guid.NewGuid(), head)));
        Assert.Null(AccessReviewController.ReviewerFor(P(head, head)));   // departman başının kendisi → İK
        Assert.Null(AccessReviewController.ReviewerFor(null));
    }
}
