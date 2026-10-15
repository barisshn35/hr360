using EngagementService.Controllers;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;
using EngagementService.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Engagement.Tests;

/// <summary>Sohbet botunun iç uçları (/api/internal/chat/*) ve paylaşılan kural çekirdekleri.</summary>
public class InternalChatTests
{
    private static InternalChatController Controller(TenantContext tenant, string? header)
    {
        var http = new DefaultHttpContext();
        if (header is not null) http.Request.Headers["X-Internal-Token"] = header;
        // DbContext'e ulaşılmadan (anahtar/kiracı denetimi) döneceği için bağlantısız bağlam yeterli.
        var db = new EngagementDbContext(new DbContextOptionsBuilder<EngagementDbContext>().UseNpgsql("Host=127.0.0.1;Database=none").Options, tenant);
        return new InternalChatController(db, tenant) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public void Anahtar_karsilastirma()
    {
        Assert.False(InternalChatController.TokenMatches(null, "x"));
        Assert.False(InternalChatController.TokenMatches("", ""));
        Assert.False(InternalChatController.TokenMatches("gizli", null));
        Assert.False(InternalChatController.TokenMatches("gizli", "gizli2"));
        Assert.True(InternalChatController.TokenMatches("gizli", "gizli"));
    }

    [Fact]
    public async Task Anahtar_yoksa_ya_da_yanlissa_uc_404_doner_ve_kiraci_ayarlanmaz()
    {
        var before = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        try
        {
            var body = new InternalChatController.ChatKudosInput("acme", Guid.NewGuid(), Guid.NewGuid(), "Teşekkürler", null, "Slack");

            // Sunucuda anahtar tanımlı değil → uç kapalı.
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null);
            var t1 = new TenantContext();
            Assert.IsType<NotFoundResult>(await Controller(t1, "herhangi").CreateKudos(body, null!, default));
            Assert.Null(t1.TenantSlug);

            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", "test-anahtar-123");
            var t2 = new TenantContext();
            Assert.IsType<NotFoundResult>(await Controller(t2, null).CreateKudos(body, null!, default));
            Assert.IsType<NotFoundResult>(await Controller(t2, "yanlis").DeskCancel(new("acme", Guid.NewGuid(), Guid.NewGuid(), null), default));
            Assert.IsType<NotFoundResult>(await Controller(t2, "yanlis").PulseAnswer(new("acme", Guid.NewGuid(), 3, null), default));
            Assert.IsType<NotFoundResult>(await Controller(t2, "yanlis").SaveExitInterview(new("acme", Guid.NewGuid(), Guid.NewGuid(), new ExitInterview(), null), null!, default));
            Assert.Null(t2.TenantSlug);

            // Doğru anahtar ama kiracı yok → 400.
            var t3 = new TenantContext();
            var bad = await Controller(t3, "test-anahtar-123").PulseClose(new(null, Guid.NewGuid()), default);
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(bad).StatusCode);

            // Doğru anahtar, geçersiz soru → kural hatası; kiracı istek bağlamına yazılmış olur.
            var t4 = new TenantContext();
            var shortQ = await Controller(t4, "test-anahtar-123").PulseSurvey(new("acme", "Kısa", null, null, "İK"), default);
            Assert.IsType<BadRequestObjectResult>(shortQ);
            Assert.Equal("acme", t4.TenantSlug);
            Assert.False(t4.IsPlatformAdmin);
        }
        finally { Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", before); }
    }

    [Theory]
    [InlineData("", "thanks", "message_length")]
    [InlineData("   ", "thanks", "message_length")]
    [InlineData("ok", "olmayan-rozet", "badge")]
    [InlineData("ok", null, "badge")]
    public void Takdir_kurallari(string message, string? badge, string code) =>
        Assert.Equal(code, KudosController.Validate(message, badge)?.Code);

    [Fact]
    public void Takdir_gecerli_ve_500_karakter_siniri()
    {
        Assert.Null(KudosController.Validate("Sunum için teşekkürler", "thanks"));
        Assert.Null(KudosController.Validate(new string('a', 500), "teamwork"));
        Assert.Equal(400, KudosController.Validate(new string('a', 501), "thanks")!.Status);
    }

    [Fact]
    public void Masa_rezervasyon_tarih_ve_saat_kurallari()
    {
        var today = new DateOnly(2026, 10, 5);
        Assert.Null(WorkplaceController.ValidateBooking(today, 540, 1080, today));
        Assert.Null(WorkplaceController.ValidateBooking(today.AddDays(30), 540, 1080, today));
        Assert.Equal("date_range", WorkplaceController.ValidateBooking(today.AddDays(-1), 540, 1080, today)?.Code);
        Assert.Equal("date_range", WorkplaceController.ValidateBooking(today.AddDays(31), 540, 1080, today)?.Code);
        Assert.Equal("time_range", WorkplaceController.ValidateBooking(today, 1080, 540, today)?.Code);
        Assert.Equal("time_range", WorkplaceController.ValidateBooking(today, 0, 24 * 60 + 1, today)?.Code);
    }

    [Fact]
    public void Nabiz_anketi_tek_soru_olcek_1_5()
    {
        var send = new DateTime(2026, 10, 4, 22, 30, 0, DateTimeKind.Utc); // TR 5 Ekim 01:30
        var s = InternalChatController.BuildPulseSurvey("Bu hafta nasılsınız?", send, send.AddDays(7), "Ayşe İK");
        Assert.Equal("Sohbet nabzı — 05.10.2026", s.Title);
        Assert.Equal(("Pulse", true, "Open"), (s.Kind, s.IsAnonymous, s.Status));
        Assert.Null(SurveysController.ValidateSurvey(s.Title, s.Questions));
        var q = Assert.Single(s.Questions);
        Assert.Equal(("pulse", "Scale", true), (q.Id, q.Type, q.Required));

        var (err, answers) = SurveysController.ValidateAnswers(s, new() { new() { QuestionId = "pulse", Score = 4, Text = "yok sayılmaz" } });
        Assert.Null(err);
        Assert.Equal(4, Assert.Single(answers).Score);
        Assert.Equal("answer", SurveysController.ValidateAnswers(s, new() { new() { QuestionId = "pulse", Score = 6 } }).Error?.Code);
        Assert.Equal("answer", SurveysController.ValidateAnswers(s, null).Error?.Code);

        Assert.Null(SurveysController.CheckOpen(s, send.AddDays(1)));
        Assert.Equal("closed", SurveysController.CheckOpen(s, send.AddDays(8))?.Code);
        s.Status = "Closed";
        Assert.Equal("closed", SurveysController.CheckOpen(s, send)?.Code);
    }

    [Fact]
    public void Nabiz_yaniti_kimliksiz_saklanir()
    {
        var now = new DateTime(2026, 10, 5, 14, 37, 12, DateTimeKind.Utc);
        var survey = Guid.NewGuid();
        var a = InternalChatController.AnonymousResponse(survey, new() { new() { QuestionId = "pulse", Score = 3 } }, now);
        var b = InternalChatController.AnonymousResponse(survey, new() { new() { QuestionId = "pulse", Score = 3 } }, now);
        Assert.StartsWith("chat-", a.RespondentKey);
        Assert.Equal(5 + 32, a.RespondentKey.Length);
        Assert.NotEqual(a.RespondentKey, b.RespondentKey);   // aynı kişi bile olsa eşleştirilemez
        Assert.Null(a.DepartmentName);                         // departman kırılımına girmez
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), a.SubmittedAt); // gün hassasiyeti
        Assert.Equal(DateTimeKind.Utc, a.SubmittedAt.Kind);
    }

    [Fact]
    public void Cikis_anketi_dogrulama()
    {
        var ok = new ExitInterview { PrimaryReason = "Ücret", ManagerScore = 4, CultureScore = 5, GrowthScore = 1, CompensationScore = 2, WouldRecommend = true };
        Assert.Null(OffboardingController.ValidateSelfInterview(ok));
        Assert.NotNull(OffboardingController.ValidateSelfInterview(null));
        Assert.NotNull(OffboardingController.ValidateSelfInterview(new ExitInterview { PrimaryReason = "Ücret", ManagerScore = 0 }));
        Assert.NotNull(OffboardingController.ValidateSelfInterview(new ExitInterview { PrimaryReason = "Ücret", CultureScore = 6 }));
        Assert.NotNull(OffboardingController.ValidateSelfInterview(new ExitInterview { PrimaryReason = " " }));
        Assert.NotNull(OffboardingController.ValidateSelfInterview(new ExitInterview { PrimaryReason = "Ücret", Comments = new string('x', 2001) }));
    }

    [Fact]
    public void Denetim_kullanici_adi_sohbet_isareti_tasir()
    {
        Assert.Equal("Ayşe Yılmaz · Sohbet (Slack)", InternalChatController.ChatUserName("Ayşe Yılmaz", "Slack"));
        Assert.Equal("Ayşe Yılmaz · Sohbet", InternalChatController.ChatUserName("Ayşe Yılmaz", null));
    }

    [Fact]
    public void Kural_hatasi_http_yanitina_cevrilir()
    {
        var r = Assert.IsType<ObjectResult>(RuleError.Conflict("Bu saatlerde zaten bir masanız var.", "has_desk").ToResult());
        Assert.Equal(409, r.StatusCode);
        Assert.Contains("has_desk", System.Text.Json.JsonSerializer.Serialize(r.Value));
    }
}
