using LeaveService.Tenancy;
using Xunit;

namespace Leave.Tests;

/// <summary>
/// Guvenlik dalgasi 2A: platform yoneticisi kiraci kapisi (PlatformAccessGate). Dosya tum
/// servislerde birebir ayni oldugu icin burada (leave-service kopyasi) sinanir.
/// </summary>
public class PlatformAccessGateTests
{
    [Theory]
    [InlineData("/health", true)]
    [InlineData("/metrics", true)]
    [InlineData("/api/internal/leave-requests", true)]
    [InlineData("/api/billing", false)]
    [InlineData("/api/plan", false)]
    [InlineData("/api/leave-requests", false)]
    [InlineData("/api/employees/me", false)]
    [InlineData("", false)]
    public void Exempt_paths(string path, bool expected) => Assert.Equal(expected, PlatformAccessGate.IsExempt(path));

    [Theory]
    [InlineData("demo", null, "demo")]
    [InlineData(null, "acme", "acme")]
    [InlineData("Demo", "acme", "demo")]
    [InlineData("  ", "acme", "acme")]
    [InlineData("demo;drop", null, null)]
    [InlineData(null, null, null)]
    public void Target_tenant_header_then_claim(string? header, string? claim, string? expected) =>
        Assert.Equal(expected, PlatformAccessGate.TargetTenant(header, claim));

    [Fact]
    public void Path_is_normalized_without_identifiers()
    {
        Assert.Equal("/api/employees/{id}/assignments",
            PlatformAccessGate.NormalizePath("/api/employees/0e879b9e-d72b-489f-aa5b-8291e0bcbefb/assignments"));
        Assert.Equal("/api/leave-requests/{id}", PlatformAccessGate.NormalizePath("/api/leave-requests/42"));
        Assert.Equal("/api/users/{id}", PlatformAccessGate.NormalizePath("/api/users/ayse@demo.hr360"));
        Assert.Equal("/api/leave-requests", PlatformAccessGate.NormalizePath("/api/leave-requests"));
    }

    [Fact]
    public void Audit_dedupes_same_key_for_ten_minutes()
    {
        var key = "g|GET|/api/x/" + Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        Assert.True(PlatformAccessGate.ShouldAudit(key, t0));
        Assert.False(PlatformAccessGate.ShouldAudit(key, t0.AddMinutes(5)));
        Assert.True(PlatformAccessGate.ShouldAudit(key, t0.AddMinutes(11)));
    }
}
