using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnboardingService.Controllers;
using OnboardingService.Data;
using OnboardingService.Models;
using OnboardingService.Tenancy;
using Xunit;

namespace Onboarding.Tests;

/// <summary>Sohbetteki "✓ görev" iç ucu: web ucuyla aynı yetki/durum kuralları ve plan kapanışı.</summary>
public class InternalChatTests
{
    private const string Token = "unit-test-internal-token";
    static InternalChatTests() => Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);

    private static readonly Guid Hire = Guid.NewGuid(), Buddy = Guid.NewGuid(), Other = Guid.NewGuid();

    private sealed class World
    {
        public required InternalChatController Ctl;
        public required OnboardingDbContext Db;
        public Guid PlanId, Own, Legal, BuddyTask, ForeignTask;
    }

    private static async Task<World> MakeAsync(string? token = Token, PlanStatus status = PlanStatus.InProgress)
    {
        var tenant = new TenantContext { TenantSlug = "demo" };
        var db = new OnboardingDbContext(new DbContextOptionsBuilder<OnboardingDbContext>()
            .UseInMemoryDatabase("onb-" + Guid.NewGuid()).Options, tenant);
        var plan = new OnboardingPlan { EmployeeId = Hire, StartDate = new DateOnly(2026, 10, 1), Status = status, BuddyEmployeeId = Buddy };
        var own = new OnboardingTask { PlanId = plan.Id, Title = "KVKK eğitimi", Category = TaskCategory.Training, OwnerRole = "Employee" };
        var legal = new OnboardingTask { PlanId = plan.Id, Title = "Sözleşme imzası", Category = TaskCategory.Legal };
        var buddyTask = new OnboardingTask { PlanId = plan.Id, Title = "Ofis turu", Category = TaskCategory.HR, AssigneeEmployeeId = Buddy };
        var otherPlan = new OnboardingPlan { EmployeeId = Other, StartDate = new DateOnly(2026, 10, 1), Status = PlanStatus.InProgress };
        var foreign = new OnboardingTask { PlanId = otherPlan.Id, Title = "Başkasının görevi", Category = TaskCategory.IT };
        db.AddRange(plan, otherPlan, own, legal, buddyTask, foreign);
        await db.SaveChangesAsync();
        tenant.TenantSlug = null; // iç uç kiracıyı istek gövdesinden kurmalı

        var ctl = new InternalChatController(db, tenant);
        var http = new DefaultHttpContext();
        if (token is not null) http.Request.Headers["X-Internal-Token"] = token;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return new World { Ctl = ctl, Db = db, PlanId = plan.Id, Own = own.Id, Legal = legal.Id, BuddyTask = buddyTask.Id, ForeignTask = foreign.Id };
    }

    private static ChatTaskDoneRequest Req(Guid emp, Guid task, string? tenant = "demo") => new(tenant, emp, task, "Slack");

    private static (int? Status, string? Message) Err(IActionResult r) => r is ObjectResult o
        ? (o.StatusCode, o.Value is null ? null : JsonSerializer.SerializeToElement(o.Value).TryGetProperty("message", out var m) ? m.GetString() : null)
        : (null, null);

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Anahtar_yoksa_ya_da_yanlissa_404(string? token)
    {
        var w = await MakeAsync(token);
        Assert.IsType<NotFoundResult>(await w.Ctl.TaskDone(Req(Hire, w.Own), default));
        Assert.Equal(OnboardingTaskStatus.Pending, (await w.Db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == w.Own)).Status);
    }

    [Fact]
    public async Task Kendi_gorevini_tamamlar_ikinci_kez_409()
    {
        var w = await MakeAsync();
        var ok = Assert.IsType<OkObjectResult>(await w.Ctl.TaskDone(Req(Hire, w.Own), default));
        var body = JsonSerializer.SerializeToElement(ok.Value);
        Assert.Equal("KVKK eğitimi", body.GetProperty("title").GetString());
        Assert.False(body.GetProperty("planCompleted").GetBoolean());
        var t = await w.Db.Tasks.IgnoreQueryFilters().SingleAsync(x => x.Id == w.Own);
        Assert.Equal(OnboardingTaskStatus.Done, t.Status);
        Assert.NotNull(t.CompletedAt);
        Assert.Equal((409, "Bu görev zaten tamamlanmış"), Err(await w.Ctl.TaskDone(Req(Hire, w.Own), default)));
    }

    [Fact]
    public async Task Hukuki_gorev_ve_baskasinin_gorevi_404()
    {
        var w = await MakeAsync();
        Assert.Equal(404, Err(await w.Ctl.TaskDone(Req(Hire, w.Legal), default)).Status);
        Assert.Equal(404, Err(await w.Ctl.TaskDone(Req(Hire, w.ForeignTask), default)).Status);
        Assert.Equal(404, Err(await w.Ctl.TaskDone(Req(Hire, Guid.NewGuid()), default)).Status);
        // Başka kiracıdan istek görevi göremez.
        Assert.Equal(404, Err(await w.Ctl.TaskDone(Req(Hire, w.Own, tenant: "baska"), default)).Status);
        Assert.Equal(OnboardingTaskStatus.Pending, (await w.Db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == w.Legal)).Status);
    }

    [Fact]
    public async Task Atanan_kisi_baskasinin_planindaki_gorevi_tamamlar_son_gorevle_plan_kapanir()
    {
        var w = await MakeAsync();
        Assert.IsType<OkObjectResult>(await w.Ctl.TaskDone(Req(Buddy, w.BuddyTask), default));
        Assert.IsType<OkObjectResult>(await w.Ctl.TaskDone(Req(Hire, w.Own), default));
        // Hukuki görevi yalnızca yöneten tamamlayabilir; burada doğrudan kapatıp son durumu sınarız.
        var legal = await w.Db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == w.Legal);
        legal.AssigneeEmployeeId = Hire;
        await w.Db.SaveChangesAsync();
        var ok = Assert.IsType<OkObjectResult>(await w.Ctl.TaskDone(Req(Hire, w.Legal), default));
        Assert.True(JsonSerializer.SerializeToElement(ok.Value).GetProperty("planCompleted").GetBoolean());
        var plan = await w.Db.Plans.IgnoreQueryFilters().SingleAsync(p => p.Id == w.PlanId);
        Assert.Equal(PlanStatus.Completed, plan.Status);
        Assert.NotNull(plan.CompletedAt);
    }

    [Fact]
    public async Task Iptal_edilmis_planda_degisiklik_yok()
    {
        var w = await MakeAsync(status: PlanStatus.Cancelled);
        Assert.Equal((400, "İptal edilmiş plandaki görev değiştirilemez"), Err(await w.Ctl.TaskDone(Req(Hire, w.Own), default)));
    }
}
