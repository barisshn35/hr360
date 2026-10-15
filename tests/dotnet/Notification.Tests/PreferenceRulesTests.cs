using NotificationService.Models;
using NotificationService.Preferences;
using Xunit;
using Msg = NotificationService.Models.Notification;

namespace Notification.Tests;

public class PreferenceRulesTests
{
    static readonly TimeZoneInfo Tz = QuietHoursCalc.Istanbul;
    const int AllDays = 127;
    const int Weekdays = 0b0111110; // Pzt..Cum

    /// <summary>İstanbul yerel saatinden UTC an (UTC+3).</summary>
    static DateTimeOffset At(int y, int m, int d, int h, int min = 0) => QuietHoursCalc.ToUtc(new DateTime(y, m, d, h, min, 0), Tz);

    [Theory]
    [InlineData("workflow.submitted", "approvals")]
    [InlineData("workflow.step-approved", "approvals")]
    [InlineData("shift.swap.approval", "approvals")]
    [InlineData("shift.swap.decision", "leave")]
    [InlineData("leave.approved", "leave")]
    [InlineData("compensation.raise", "payroll")]
    [InlineData("payroll.payslip", "payroll")]
    [InlineData("engagement.kudos", "announcements")]
    [InlineData("announcement.published", "announcements")]
    [InlineData("learning.certificate.issued", "learning")]
    [InlineData("onboarding.buddy", "learning")]
    [InlineData("recruitment.offer.sent", "recruitment")]
    [InlineData("privacy.breach", "security")]
    [InlineData("privacy.request.completed", "legal")]
    [InlineData("security.login.suspicious", "security")]
    [InlineData("account.locked", "security")]
    [InlineData("auth.test", "security")]
    [InlineData("signature.otp", "security")]
    [InlineData("disciplinary.decision", "legal")]
    [InlineData("document.request", "system")]
    [InlineData("employee.hired", "system")]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    public void Kategori_onek_kurali(string? code, string expected) =>
        Assert.Equal(expected, NotificationCategories.Categorize(code));

    [Fact]
    public void SQL_kosulu_Categorize_ile_ayni_sonucu_verir()
    {
        string?[] codes = ["workflow.submitted", "shift.swap.approval", "shift.swap.decision", "leave.rejected", "compensation.raise",
            "engagement.kudos", "learning.x", "recruitment.applied", "privacy.breach", "privacy.request", "auth.test", "security.x", "signature.otp", "document.request", null, "unknown.code"];
        foreach (var cat in NotificationCategories.All.Select(c => c.Key))
        {
            var f = NotificationCategories.InCategories([cat]).Compile();
            foreach (var code in codes)
                Assert.Equal(NotificationCategories.Categorize(code) == cat, f(new Msg { Body = "x", TemplateCode = code }));
        }
    }

    [Fact]
    public void Uygulama_ici_gizleme_zorunlu_kategoriyi_ve_e_postayi_etkilemez()
    {
        var visible = NotificationCategories.VisibleInApp(["announcements", "payroll", "legal"]).Compile();
        Assert.False(visible(new Msg { Body = "x", TemplateCode = "engagement.kudos" }));
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "engagement.kudos", Channel = NotificationChannel.Email }));
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "compensation.raise" }));
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "privacy.breach" }));
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "workflow.approved" }));
    }

    [Fact]
    public void Ayni_gun_penceresi()
    {
        // 12:00-14:00 her gün
        var s = new TimeOnly(12, 0); var e = new TimeOnly(14, 0);
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 5, 11, 59, 0), s, e, AllDays));
        Assert.Equal(new DateTime(2026, 10, 5, 14, 0, 0), QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 5, 12, 0, 0), s, e, AllDays));
        Assert.Equal(new DateTime(2026, 10, 5, 14, 0, 0), QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 5, 13, 59, 0), s, e, AllDays));
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 5, 14, 0, 0), s, e, AllDays));
    }

    [Fact]
    public void Gece_yarisini_asan_pencere()
    {
        var s = new TimeOnly(22, 0); var e = new TimeOnly(8, 0);
        // Pazartesi 23:30 → Salı 08:00
        Assert.Equal(new DateTime(2026, 10, 6, 8, 0, 0), QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 5, 23, 30, 0), s, e, AllDays));
        // Salı 03:00 (pencere pazartesi başladı) → Salı 08:00
        Assert.Equal(new DateTime(2026, 10, 6, 8, 0, 0), QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 6, 3, 0, 0), s, e, AllDays));
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 6, 8, 0, 0), s, e, AllDays));
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 6, 21, 59, 0), s, e, AllDays));
    }

    [Fact]
    public void Gun_secimi_pencerenin_basladigi_gune_bakar()
    {
        var s = new TimeOnly(22, 0); var e = new TimeOnly(8, 0);
        // 2026-10-09 Cuma, 10-10 Cumartesi, 10-11 Pazar, 10-12 Pazartesi
        Assert.NotNull(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 9, 23, 0, 0), s, e, Weekdays));  // Cuma gecesi
        Assert.NotNull(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 10, 7, 0, 0), s, e, Weekdays)); // Cmt sabahı (Cuma penceresi)
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 10, 23, 0, 0), s, e, Weekdays));   // Cmt gecesi
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 12, 7, 0, 0), s, e, Weekdays));    // Pzt sabahı (Pazar penceresi)
        Assert.NotNull(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 12, 22, 30, 0), s, e, Weekdays));
    }

    [Fact]
    public void Bos_pencere_ve_kapali_ayar_sessiz_degil()
    {
        Assert.Null(QuietHoursCalc.QuietUntilLocal(new DateTime(2026, 10, 5, 9, 0, 0), new(9, 0), new(9, 0), AllDays));
        Assert.Null(QuietHoursCalc.QuietUntil(At(2026, 10, 5, 23), new QuietHours(false, new(22, 0), new(8, 0), AllDays), Tz));
    }

    [Fact]
    public void Sessiz_saat_bitisi_UTC_olarak_hesaplanir()
    {
        var until = QuietHoursCalc.QuietUntil(At(2026, 10, 5, 23), new QuietHours(true, new(22, 0), new(8, 0), AllDays), Tz);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero), until); // 08:00 TRT = 05:00 UTC
    }

    static EffectivePrefs Prefs(bool quiet = false, bool digest = false, params (string Cat, ChannelPrefs Ch)[] cats) =>
        new(cats.ToDictionary(c => c.Cat, c => c.Ch), new QuietHours(quiet, new(22, 0), new(8, 0), AllDays), digest, 18);

    [Fact]
    public void E_posta_karari()
    {
        var day = At(2026, 10, 5, 12);
        var night = At(2026, 10, 5, 23);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideEmail("leave.approved", false, null, night).Decision);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideEmail("leave.approved", false, Prefs(quiet: true), day).Decision);
        var (d, until) = DeliveryRules.DecideEmail("leave.approved", false, Prefs(quiet: true), night);
        Assert.Equal(Delivery.Defer, d);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero), until);
        // Kategori e-postası kapalı → gönderilmez (zorunlu kategoride de e-posta kapatılabilir).
        var off = new ChannelPrefs(true, false, true, true);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecideEmail("leave.approved", false, Prefs(false, false, ("leave", off)), day).Decision);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecideEmail("compensation.raise", false, Prefs(false, false, ("payroll", off)), day).Decision);
        // Özet: acil olmayan kategori özete girer; onay ve eylem bağlantılı e-posta girmez.
        Assert.Equal(Delivery.Digest, DeliveryRules.DecideEmail("learning.x", false, Prefs(digest: true), day).Decision);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideEmail("workflow.submitted", false, Prefs(digest: true), day).Decision);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideEmail("learning.x", true, Prefs(digest: true), day).Decision);
        // Hesap e-postaları tercihlerden bağımsız.
        Assert.Equal(Delivery.Send, DeliveryRules.DecideEmail("employee.hired", false, Prefs(true, true, ("system", new(true, false, false, false))), night).Decision);
        // Özet e-postasının kendisi sessiz saate uyar ama tekrar özete girmez.
        Assert.Equal(Delivery.Defer, DeliveryRules.DecideEmail(NotificationCategories.DigestCode, false, Prefs(true, true), night).Decision);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideEmail(NotificationCategories.DigestCode, false, Prefs(false, true), night).Decision);
    }

    [Fact]
    public void Push_karari_ve_zorunlu_uygulama_ici()
    {
        var day = At(2026, 10, 5, 12);
        var night = At(2026, 10, 5, 23);
        Assert.Equal(Delivery.Send, DeliveryRules.DecidePush("engagement.kudos", Prefs(), day).Decision);
        Assert.Equal(Delivery.Defer, DeliveryRules.DecidePush("engagement.kudos", Prefs(quiet: true), night).Decision);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecidePush("engagement.kudos", Prefs(false, false, ("announcements", new(true, true, false, true))), day).Decision);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecidePush("engagement.kudos", Prefs(false, false, ("announcements", new(false, true, true, true))), day).Decision);
        // Zorunlu kategoride uygulama içi kapatılamaz → push tercihi geçerli.
        var p = Prefs(false, false, ("legal", new(false, false, true, false)));
        Assert.True(p.For("legal").InApp);
        Assert.Equal(Delivery.Send, DeliveryRules.DecidePush("disciplinary.decision", p, day).Decision);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecidePush("disciplinary.decision", Prefs(false, false, ("legal", new(true, true, false, true))), day).Decision);
        // Deneme bildirimi (kod yok) her zaman gider.
        Assert.Equal(Delivery.Send, DeliveryRules.DecidePush(null, Prefs(quiet: true), night).Decision);
    }

    [Fact]
    public void Guvenlik_acisindan_kritik_bildirim_opt_out_sessiz_saat_ve_ozeti_atlar()
    {
        var night = At(2026, 10, 5, 23);
        var allOff = new ChannelPrefs(false, false, false, false);
        // Kişi her şeyi kapatmış, sessiz saatte ve özet açık.
        var p = Prefs(true, true, ("security", allOff), ("legal", allOff), ("system", allOff), ("announcements", allOff));
        foreach (var code in new[] { "privacy.breach", "security.login.suspicious", "account.locked", "auth.password-reset", "signature.otp", "x.otp" })
        {
            Assert.True(NotificationCategories.IsSecurityCritical(code), code);
            Assert.Equal((Delivery.Send, (DateTimeOffset?)null), DeliveryRules.DecideEmail(code, false, p, night));
            Assert.Equal((Delivery.Send, (DateTimeOffset?)null), DeliveryRules.DecidePush(code, p, night));
        }
        Assert.Equal(ChannelPrefs.AllOn, p.For("security"));
        foreach (var code in new[] { "privacy.request", "disciplinary.decision", "engagement.kudos", null, "" })
            Assert.False(NotificationCategories.IsSecurityCritical(code), code);
        // Normal bildirim aynı tercihlerle kapalı / ertelenir.
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecideEmail("disciplinary.decision", false, p, night).Decision);
        Assert.Equal(Delivery.Defer, DeliveryRules.DecideEmail("workflow.submitted", false, p, night).Decision);
        Assert.Equal(Delivery.Digest, DeliveryRules.DecideEmail("leave.approved", false, p, night).Decision);
        // Uygulama içinde gizlenemez (kategori kapatılmış olsa bile).
        var visible = NotificationCategories.VisibleInApp(["security", "announcements"]).Compile();
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "privacy.breach" }));
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "account.locked" }));
        Assert.True(visible(new Msg { Body = "x", TemplateCode = "engagement.otp" }));
        Assert.False(visible(new Msg { Body = "x", TemplateCode = "engagement.kudos" }));
    }

    [Fact]
    public void Ozet_zamani()
    {
        // Özet saati 18:00
        var last = At(2026, 10, 5, 18, 1);
        Assert.False(QuietHoursCalc.DigestDue(At(2026, 10, 5, 20), 18, last, Tz));
        Assert.False(QuietHoursCalc.DigestDue(At(2026, 10, 6, 17, 59), 18, last, Tz));
        Assert.True(QuietHoursCalc.DigestDue(At(2026, 10, 6, 18), 18, last, Tz));
        Assert.True(QuietHoursCalc.DigestDue(At(2026, 10, 6, 2), 18, At(2026, 10, 4, 19), Tz)); // dünün özeti kaçmış
        Assert.True(QuietHoursCalc.DigestDue(At(2026, 10, 6, 2), 0, At(2026, 10, 5, 23), Tz));
        Assert.True(QuietHoursCalc.DigestDue(At(2026, 10, 6, 2), 18, null, Tz));
    }

    [Fact]
    public void Ozet_yalnizca_konulari_kategoriye_gore_gruplar()
    {
        var t = At(2026, 10, 5, 9);
        var items = new List<DigestItem>
        {
            new("learning.certificate.issued", "Sertifikanız hazır", t.AddMinutes(30)),
            new("engagement.kudos", "Takdir aldınız", t),
            new("learning.course.assigned", "Yeni eğitim atandı", t.AddMinutes(10)),
            new("leave.approved", null, t.AddMinutes(5)),
        };
        var (subject, body) = DigestBuilder.Build(items, "tr", Tz);
        Assert.Equal("Günlük bildirim özeti (4)", subject);
        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var leave = lines.IndexOf("İzin ve vardiya (1)");
        var ann = lines.IndexOf("Duyurular ve etkileşim (1)");
        var learn = lines.IndexOf("Eğitim ve oryantasyon (2)");
        Assert.True(leave > 0 && ann > leave && learn > ann, body);
        Assert.Equal("• 09:10 Yeni eğitim atandı", lines[learn + 1]);
        Assert.Equal("• 09:30 Sertifikanız hazır", lines[learn + 2]);
        Assert.Equal("• 09:05 Bildirim", lines[leave + 1]);
        var (en, _) = DigestBuilder.Build(items, "en", Tz);
        Assert.Equal("Daily notification summary (4)", en);
    }

    [Fact]
    public void Ozet_satir_siniri()
    {
        var t = At(2026, 10, 5, 9);
        var items = Enumerable.Range(0, DigestBuilder.MaxLines + 5).Select(i => new DigestItem("learning.x", $"Konu {i}", t)).ToList();
        var (_, body) = DigestBuilder.Build(items, "tr", Tz);
        Assert.Contains("…ve 5 bildirim daha.", body);
    }
}
