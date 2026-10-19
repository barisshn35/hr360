using HR360.Platform;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Notification.Tests;

/// <summary>Ortak günlük kapsamı (apps/shared-dotnet/StructuredLogging.cs): istek kimliği ve şirket.</summary>
public class StructuredLoggingTests
{
    static HttpContext Ctx(string? header)
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "trace-1" };
        if (header is not null) ctx.Request.Headers["X-Correlation-Id"] = header;
        return ctx;
    }

    [Fact]
    public void Gateway_id_is_used() => Assert.Equal("a1b2c3d4e5f6", StructuredLogging.CorrelationId(Ctx("a1b2c3d4e5f6")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\nline")]
    [InlineData("x\"y")]
    public void Unsafe_or_missing_header_falls_back_to_trace_id(string? header) =>
        Assert.Equal("trace-1", StructuredLogging.CorrelationId(Ctx(header)));

    [Fact]
    public void Too_long_header_falls_back() =>
        Assert.Equal("trace-1", StructuredLogging.CorrelationId(Ctx(new string('a', 65))));

    [Fact]
    public async Task Tenant_is_read_when_logging_not_when_scope_opens()
    {
        await Task.Run(() =>
        {
            var scope = new StructuredLogging.RequestScope("cid-1");
            Assert.Single(scope);
            TenantSession.Current = "acme";
            Assert.Equal(2, scope.Count);
            Assert.Contains(scope, kv => kv.Key == "Tenant" && (string?)kv.Value == "acme");
            Assert.Equal("cid=cid-1 tenant=acme", scope.ToString());
        });
    }
}
