using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkflowService.Data;
using WorkflowService.Models;
using WorkflowService.Services;
using WorkflowService.Tenancy;
using Xunit;

namespace Workflow.Tests;

/// <summary>Onay yönlendirme (Services/WorkflowRouting.cs): koşullu adımlar, zincir kurma, vekâlet, e-posta karar jetonu.</summary>
public class RoutingTests
{
    const string Tenant = "t-routing";

    static WorkflowDbContext Db(string? name = null) =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options,
            new TenantContext { TenantSlug = Tenant });

    static DefinitionStep Cond(ConditionField f, string op, decimal v) =>
        new() { Approver = ApproverKind.Employee, EmployeeId = Guid.NewGuid(), ConditionField = f, ConditionOp = op, ConditionValue = v };

    [Theory]
    [InlineData("""{"days":5}""", ConditionField.Days, 5)]
    [InlineData("""{"Amount":1250.5}""", ConditionField.Amount, 1250.5)]
    [InlineData("""{"totalAmount":10}""", ConditionField.Amount, 10)]
    [InlineData("""{"hours":3}""", ConditionField.Hours, 3)]
    public void PayloadNumber_reads_known_fields(string payload, ConditionField f, decimal want) =>
        Assert.Equal(want, WorkflowRouting.PayloadNumber(payload, f));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bozuk{")]
    [InlineData("""{"days":"5"}""")]
    [InlineData("""{"other":5}""")]
    public void PayloadNumber_missing_or_invalid_is_null(string? payload) =>
        Assert.Null(WorkflowRouting.PayloadNumber(payload, ConditionField.Days));

    [Theory]
    [InlineData(">", 5, false)]
    [InlineData(">=", 5, true)]
    [InlineData("<", 6, true)]
    [InlineData("<=", 4, false)]
    public void ConditionHolds_compares(string op, decimal value, bool want) =>
        Assert.Equal(want, WorkflowRouting.ConditionHolds(Cond(ConditionField.Days, op, value), """{"days":5}"""));

    [Fact]
    public void Unconditional_step_always_holds_but_conditional_step_without_field_is_skipped()
    {
        Assert.True(WorkflowRouting.ConditionHolds(new DefinitionStep { Approver = ApproverKind.Employee }, null));
        Assert.False(WorkflowRouting.ConditionHolds(Cond(ConditionField.Amount, ">", 0), """{"days":5}"""));
    }

    [Fact]
    public async Task Resolve_without_active_definition_is_null()
    {
        await using var db = Db();
        db.Definitions.Add(new WorkflowDefinition { Type = WorkflowType.LeaveRequest, IsActive = false, StepsJson = "[]" });
        await db.SaveChangesAsync();
        Assert.Null(await new WorkflowRouting(db).ResolveAsync(Tenant, WorkflowType.LeaveRequest, Guid.NewGuid(), null, default));
    }

    [Fact]
    public async Task Resolve_builds_chain_skipping_requester_repeats_and_unmet_conditions()
    {
        var requester = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var steps = new List<DefinitionStep>
        {
            new() { Approver = ApproverKind.Employee, EmployeeId = requester },               // talep eden atlanır
            new() { Approver = ApproverKind.Employee, EmployeeId = a, SlaHours = 24 },
            new() { Approver = ApproverKind.Employee, EmployeeId = a },                       // art arda aynı kişi atlanır
            new() { Approver = ApproverKind.Employee, EmployeeId = Guid.NewGuid(),
                    ConditionField = ConditionField.Amount, ConditionOp = ">", ConditionValue = 10_000 }, // koşul sağlanmaz
            new() { Approver = ApproverKind.Employee, EmployeeId = b, SlaHours = 48 },
        };
        await using var db = Db();
        db.Definitions.Add(new WorkflowDefinition
        {
            Type = WorkflowType.ExpenseClaim, IsActive = true, StepsJson = JsonSerializer.Serialize(steps, WorkflowRouting.Json),
        });
        await db.SaveChangesAsync();

        var chain = await new WorkflowRouting(db).ResolveAsync(Tenant, WorkflowType.ExpenseClaim, requester, """{"amount":500}""", default);
        Assert.Equal(new List<(Guid, int?)> { (a, 24), (b, 48) }, chain);
    }

    [Fact]
    public async Task Active_delegation_only_within_dates_and_not_revoked()
    {
        var from = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        await using var db = Db();
        db.Delegations.AddRange(
            new Delegation { FromEmployeeId = from, ToEmployeeId = Guid.NewGuid(), StartDate = today.AddDays(-10), EndDate = today.AddDays(-1) },
            new Delegation { FromEmployeeId = from, ToEmployeeId = Guid.NewGuid(), StartDate = today, EndDate = today, RevokedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var routing = new WorkflowRouting(db);
        Assert.Null(await routing.ActiveDelegationAsync(from, default));

        var to = Guid.NewGuid();
        db.Delegations.Add(new Delegation { FromEmployeeId = from, ToEmployeeId = to, StartDate = today.AddDays(-1), EndDate = today.AddDays(1) });
        await db.SaveChangesAsync();
        Assert.Equal(to, (await routing.ActiveDelegationAsync(from, default))?.ToEmployeeId);
    }

    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Fact]
    public async Task Email_action_token_must_match_and_be_unexpired()
    {
        await using var db = Db();
        var wf = new WorkflowRequest { Type = WorkflowType.LeaveRequest, RequesterEmployeeId = Guid.NewGuid() };
        var step = new ApprovalStep
        {
            WorkflowRequestId = wf.Id, ApproverEmployeeId = Guid.NewGuid(),
            ActionTokenHash = Sha("gizli"), ActionTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        wf.Steps.Add(step);
        db.WorkflowRequests.Add(wf);
        await db.SaveChangesAsync();
        var routing = new WorkflowRouting(db);

        Assert.Equal(step.Id, (await routing.StepForTokenAsync($"{step.Id:N}.gizli", default))?.Id);
        Assert.Null(await routing.StepForTokenAsync($"{step.Id:N}.yanlis", default));
        Assert.Null(await routing.StepForTokenAsync($"{step.Id}.gizli", default));   // tireli kimlik kabul edilmez
        Assert.Null(await routing.StepForTokenAsync("gizli", default));
        Assert.Null(await routing.StepForTokenAsync(null!, default));

        step.ActionTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Null(await routing.StepForTokenAsync($"{step.Id:N}.gizli", default));
    }
}
