using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Controllers;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Tenancy;
using Xunit;

namespace Notification.Tests;

/// <summary>İzleme alarmı e-postası (Alertmanager webhook → POST /api/internal/alerts).</summary>
public class InternalAlertsTests
{
    const string Token = "unit-test-internal-token";

    public InternalAlertsTests() => Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);

    static (InternalAlertsController C, NotificationDbContext Db, TenantContext Tenant) Make(string? authorization = "Bearer " + Token)
    {
        var tenant = new TenantContext();
        var db = new NotificationDbContext(new DbContextOptionsBuilder<NotificationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var ctx = new DefaultHttpContext();
        if (authorization is not null) ctx.Request.Headers.Authorization = authorization;
        return (new InternalAlertsController(db) { ControllerContext = new ControllerContext { HttpContext = ctx } }, db, tenant);
    }

    static AlertmanagerPayload Payload(params (string Status, string Name, string Severity)[] alerts) => new()
    {
        Status = alerts.Any(a => a.Status == "firing") ? "firing" : "resolved",
        CommonLabels = alerts.Select(a => a.Name).Distinct().Count() == 1 ? new() { ["alertname"] = alerts[0].Name } : new(),
        Alerts = alerts.Select(a => new AlertmanagerAlert
        {
            Status = a.Status,
            Labels = new() { ["alertname"] = a.Name, ["service"] = "leave-service", ["severity"] = a.Severity },
            Annotations = new() { ["summary"] = "leave-service 2 dakikadır yanıt vermiyor" },
            StartsAt = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero),
        }).ToList(),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer yanlis")]
    [InlineData(Token)] // "Bearer " öneki olmadan
    public async Task Yanlis_anahtar_404(string? auth)
    {
        var (c, db, t) = Make(auth);
        Assert.IsType<NotFoundResult>(await c.Receive("ops@a.test", Payload(("firing", "ServisErisilemiyor", "critical")), t, default));
        Assert.Equal(0, await db.Notifications.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Her_aliciya_platform_e_postasi_kuyruga_girer()
    {
        var (c, db, t) = Make();
        var r = await c.Receive("ops@a.test, it@b.test,ops@a.test", Payload(("firing", "ServisErisilemiyor", "critical")), t, default);
        Assert.IsType<OkObjectResult>(r);
        var list = await db.Notifications.IgnoreQueryFilters().OrderBy(n => n.RecipientEmail).ToListAsync();
        Assert.Equal(["it@b.test", "ops@a.test"], list.Select(n => n.RecipientEmail));
        Assert.All(list, n =>
        {
            Assert.Equal("platform", n.TenantSlug);
            Assert.Equal(NotificationChannel.Email, n.Channel);
            Assert.Equal(Guid.Empty, n.RecipientEmployeeId);
            Assert.Equal("security.monitoring-alert", n.TemplateCode); // tercihle kapatılamaz
            Assert.Equal("[HR360] KRİTİK: ServisErisilemiyor", n.Subject);
            Assert.Contains("leave-service 2 dakikadır yanıt vermiyor", n.Body);
            Assert.Contains("20.10.2026 12:30 (İstanbul)", n.Body);
        });
    }

    [Fact]
    public async Task Alicisiz_istek_reddedilir()
    {
        var (c, db, t) = Make();
        Assert.IsType<BadRequestObjectResult>(await c.Receive("gecersiz, <x@y.z>", Payload(("firing", "A", "warning")), t, default));
        Assert.Equal(0, await db.Notifications.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public void Alici_listesi_suzulur_ve_sinirlanir()
    {
        Assert.Equal(["a@b.test"], InternalAlertsController.Recipients("a@b.test,,  ,Ali <c@d.test>,bozuk"));
        var many = string.Join(',', Enumerable.Range(1, 15).Select(i => $"u{i}@a.test"));
        Assert.Equal(InternalAlertsController.MaxRecipients, InternalAlertsController.Recipients(many).Count);
        Assert.Empty(InternalAlertsController.Recipients(null));
    }

    [Fact]
    public void Konu_duzeldi_ve_coklu_alarm()
    {
        Assert.Equal("[HR360] Düzeldi: YedekEski", InternalAlertsController.Render(Payload(("resolved", "YedekEski", "warning"))).Subject);
        var (subject, body) = InternalAlertsController.Render(Payload(("firing", "YavasYanit", "warning"), ("firing", "YavasYanit", "warning"), ("resolved", "YavasYanit", "warning")));
        Assert.Equal("[HR360] Uyarı: YavasYanit (2 alarm)", subject);
        Assert.StartsWith("2 alarm etkin.", body);
        Assert.Contains("• Düzeldi — YavasYanit (leave-service)", body);
    }

    [Fact]
    public void Uzun_govde_kirpilir()
    {
        var alerts = Enumerable.Range(0, 40).Select(_ => ("firing", "DiskDoluyor", "warning")).ToArray();
        var p = Payload(alerts);
        foreach (var a in p.Alerts!) a.Annotations!["description"] = new string('x', 500);
        var (_, body) = InternalAlertsController.Render(p);
        Assert.True(body.Length <= InternalChatController.MaxBody);
    }
}
