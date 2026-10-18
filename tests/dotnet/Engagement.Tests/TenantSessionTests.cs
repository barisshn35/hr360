using HR360.Platform;
using Xunit;

namespace Engagement.Tests;

public class TenantSessionTests
{
    [Fact]
    public void Blank_tenant_is_stored_as_null()
    {
        TenantSession.Current = "  ";
        Assert.Null(TenantSession.Current);
    }

    [Fact]
    public void AllTenants_clears_and_restores()
    {
        TenantSession.Current = "acme";
        using (TenantSession.AllTenants())
            Assert.Null(TenantSession.Current);
        Assert.Equal("acme", TenantSession.Current);
        TenantSession.Current = null;
    }

    [Fact]
    public async Task Value_flows_into_awaited_work_but_not_into_other_flows()
    {
        TenantSession.Current = "acme";
        var inner = await Task.Run(() => TenantSession.Current);
        Assert.Equal("acme", inner);
        var other = await Task.Factory.StartNew(() => TenantSession.Current, CancellationToken.None,
            TaskCreationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
        Assert.Equal("acme", other);
        TenantSession.Current = null;
        string? fresh = "x";
        var t = new Thread(() => fresh = TenantSession.Current);
        System.Threading.ExecutionContext.SuppressFlow();
        try { t.Start(); } finally { System.Threading.ExecutionContext.RestoreFlow(); }
        t.Join();
        Assert.Null(fresh);
    }
}
