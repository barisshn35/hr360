using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LeaveService.Controllers;
using LeaveService.Data;
using LeaveService.Models;
using LeaveService.Services;
using LeaveService.Tenancy;
using Xunit;

namespace Leave.Tests;

/// <summary>
/// Sohbet botunun izin iptali (POST /api/internal/chat/leave-cancel) ve web iptal ucunun
/// ortak kuralı (CancelCoreAsync): anahtar, sahiplik, yalnızca sonuçlanmamış talep,
/// bekleyen gün düşümü ve onay akışının servisler arası kapatılması.
/// </summary>
public class InternalChatCancelTests
{
    const string Token = "unit-test-internal-token";
    const string Tenant = "acme";
    static readonly Guid Emp = Guid.NewGuid();

    public InternalChatCancelTests() => Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);

    sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string? Token, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-Internal-Token", out var v) ? v.First() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    sealed record Ctx(LeaveRequestsController Controller, LeaveDbContext Db, TenantContext Tenant, StubHandler Http);

    static Ctx Make(string? headerToken = Token, string dbName = "")
    {
        var tenant = new TenantContext { TenantSlug = Tenant };
        var db = new LeaveDbContext(new DbContextOptionsBuilder<LeaveDbContext>()
            .UseInMemoryDatabase(string.IsNullOrEmpty(dbName) ? Guid.NewGuid().ToString() : dbName).Options, tenant);
        var http = new StubHandler();
        var c = new LeaveRequestsController(db, new ApprovalWorkflowClient(new HttpClient(http), new HttpContextAccessor()));
        var ctx = new DefaultHttpContext();
        if (headerToken is not null) ctx.Request.Headers["X-Internal-Token"] = headerToken;
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        // Aynı kapsamlı TenantContext: iç uç kiracıyı gövdeden buna yazar, EF filtresi bunu okur.
        return new Ctx(c, db, tenant, http);
    }

    static async Task<(LeaveRequest Leave, LeaveBalance Balance)> SeedAsync(LeaveDbContext db, LeaveRequestStatus status, Guid? wf = null, Guid? owner = null)
    {
        var leave = new LeaveRequest
        {
            TenantSlug = Tenant, EmployeeId = owner ?? Emp, Type = LeaveType.Annual,
            StartDate = new DateOnly(2026, 11, 2), EndDate = new DateOnly(2026, 11, 4), Days = 3,
            Status = status, WorkflowRequestId = wf,
        };
        var bal = new LeaveBalance { TenantSlug = Tenant, EmployeeId = owner ?? Emp, Year = 2026, Type = LeaveType.Annual, EntitledDays = 14, PendingDays = 3 };
        db.LeaveRequests.Add(leave);
        db.LeaveBalances.Add(bal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (leave, bal);
    }

    static int? Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    static string? Message(IActionResult r) =>
        (r as ObjectResult)?.Value?.GetType().GetProperty("message")?.GetValue((r as ObjectResult)!.Value) as string;

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task Rejects_missing_or_wrong_token_with_404(string? header)
    {
        var x = Make(header);
        var (leave, _) = await SeedAsync(x.Db, LeaveRequestStatus.Submitted);
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(Tenant, Emp, leave.Id), x.Tenant, default);
        Assert.IsType<NotFoundResult>(r);
        Assert.Equal(LeaveRequestStatus.Submitted, (await x.Db.LeaveRequests.AsNoTracking().FirstAsync()).Status);
    }

    [Fact]
    public async Task Disabled_when_server_has_no_token()
    {
        var x = Make();
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null);
        try
        {
            var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(Tenant, Emp, Guid.NewGuid()), x.Tenant, default);
            Assert.IsType<NotFoundResult>(r);
        }
        finally { Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token); }
    }

    [Fact]
    public async Task Requires_tenant()
    {
        var x = Make();
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(" ", Emp, Guid.NewGuid()), x.Tenant, default);
        Assert.Equal(400, Status(r));
    }

    [Fact]
    public async Task Cancels_submitted_request_releases_pending_days_and_closes_workflow_internally()
    {
        var x = Make();
        var wf = Guid.NewGuid();
        var (leave, _) = await SeedAsync(x.Db, LeaveRequestStatus.Submitted, wf);
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(Tenant, Emp, leave.Id, "Slack"), x.Tenant, default);

        Assert.Equal(200, Status(r));
        Assert.Equal(LeaveRequestStatus.Cancelled, (await x.Db.LeaveRequests.AsNoTracking().FirstAsync()).Status);
        Assert.Equal(0m, (await x.Db.LeaveBalances.AsNoTracking().FirstAsync()).PendingDays);
        // Kullanıcı jetonu yok: akış workflow-service'in iç ucuyla, talep sahibi adına kapatılır.
        var call = Assert.Single(x.Http.Calls);
        Assert.EndsWith($"/api/internal/workflows/{wf}/cancel", call.Url);
        Assert.Equal(Token, call.Token);
        Assert.Contains(Emp.ToString(), call.Body);
        Assert.Contains(Tenant, call.Body);
        // Otomatik denetim satırları kişiye yazılsın.
        Assert.Equal($"employee:{Emp}", x.Controller.HttpContext.User.FindFirst("sub")?.Value);
        Assert.Equal("Sohbet (Slack)", x.Controller.HttpContext.User.FindFirst("name")?.Value);
    }

    [Fact]
    public async Task Draft_request_is_cancelled_without_touching_balance()
    {
        var x = Make();
        var (leave, _) = await SeedAsync(x.Db, LeaveRequestStatus.Draft);
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(Tenant, Emp, leave.Id), x.Tenant, default);
        Assert.Equal(200, Status(r));
        Assert.Equal(3m, (await x.Db.LeaveBalances.AsNoTracking().FirstAsync()).PendingDays);
        Assert.Empty(x.Http.Calls);
    }

    [Theory]
    [InlineData(LeaveRequestStatus.Approved)]
    [InlineData(LeaveRequestStatus.Rejected)]
    [InlineData(LeaveRequestStatus.Cancelled)]
    public async Task Decided_or_cancelled_request_is_409_and_unchanged(LeaveRequestStatus status)
    {
        var x = Make();
        var (leave, _) = await SeedAsync(x.Db, status, Guid.NewGuid());
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(Tenant, Emp, leave.Id), x.Tenant, default);
        Assert.Equal(409, Status(r));
        Assert.False(string.IsNullOrEmpty(Message(r)));
        Assert.Equal(status, (await x.Db.LeaveRequests.AsNoTracking().FirstAsync()).Status);
        Assert.Equal(3m, (await x.Db.LeaveBalances.AsNoTracking().FirstAsync()).PendingDays);
        Assert.Empty(x.Http.Calls);
    }

    [Fact]
    public async Task Someone_elses_request_is_404()
    {
        var x = Make();
        var (leave, _) = await SeedAsync(x.Db, LeaveRequestStatus.Submitted, owner: Guid.NewGuid());
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest(Tenant, Emp, leave.Id), x.Tenant, default);
        Assert.Equal(404, Status(r));
        Assert.Equal(LeaveRequestStatus.Submitted, (await x.Db.LeaveRequests.AsNoTracking().FirstAsync()).Status);
    }

    [Fact]
    public async Task Other_tenants_request_is_404()
    {
        var x = Make();
        var (leave, _) = await SeedAsync(x.Db, LeaveRequestStatus.Submitted);
        var r = await x.Controller.CancelInternal(new InternalCancelLeaveRequest("other-co", Emp, leave.Id), x.Tenant, default);
        Assert.Equal(404, Status(r));
    }

    [Fact]
    public async Task Web_cancel_uses_same_rule_and_user_bearer_path()
    {
        var x = Make(headerToken: null);
        var wf = Guid.NewGuid();
        var (leave, _) = await SeedAsync(x.Db, LeaveRequestStatus.Submitted, wf);
        x.Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "hr-admin") }, "test"));
        var r = await x.Controller.Cancel(leave.Id, default);
        Assert.IsType<OkObjectResult>(r);
        Assert.Equal(0m, (await x.Db.LeaveBalances.AsNoTracking().FirstAsync()).PendingDays);
        var call = Assert.Single(x.Http.Calls);
        Assert.EndsWith($"/api/workflows/{wf}/cancel", call.Url);
        Assert.Null(call.Token);

        // İkinci iptal: web ucu eski biçimde düz metinli 400 döner.
        var again = await x.Controller.Cancel(leave.Id, default);
        var bad = Assert.IsType<BadRequestObjectResult>(again);
        Assert.Equal("Talep zaten iptal edilmiş", bad.Value);
    }
}
