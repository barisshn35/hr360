using System.Net;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>
/// Açık API REST hook hedefi: kiracının kendi iç ağı (özel IP, kendi n8n'i) serbest; loopback,
/// link-local/bulut meta veri, 0.0.0.0 ve HR360 altyapı servisleri engellenir.
/// </summary>
public class RestHookTargetGuardTests
{
    [Theory]
    [InlineData("https://hooks.zapier.com/hooks/catch/1/a")]
    [InlineData("http://chatmock:8000/hooks/test5d-n8n")]
    [InlineData("http://n8n:5678/webhook/x")]
    [InlineData("http://10.0.0.5:5678/webhook/x")]
    [InlineData("http://192.168.1.20/webhook/x")]
    [InlineData("http://172.20.0.9/webhook/x")]
    [InlineData("http://[fd00::5]/x")]
    [InlineData("https://n8n.acme.com.tr/webhook/x")]
    [InlineData("http://governance-service:8080/api/webhooks/inbox/abc123")]
    public void Allows_tenant_network_and_external(string url) => Assert.Null(WebhookTargetGuard.ValidateRestHook(url));

    [Theory]
    [InlineData("ftp://hooks.example.com/x")]
    [InlineData("gecersiz")]
    [InlineData("http://localhost:5678/x")]
    [InlineData("http://api.localhost/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://127.1.2.3/x")]
    [InlineData("http://2130706433/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://0.0.0.0:8080/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[fe80::1]/x")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://100.100.100.200/latest/meta-data")]
    [InlineData("http://postgres:5432/")]
    [InlineData("http://keycloak:8080/admin")]
    [InlineData("http://redis:6379/")]
    [InlineData("http://valkey:6379/")]
    [InlineData("http://kafka:9092/")]
    [InlineData("http://minio:9000/")]
    [InlineData("http://gateway/api/x")]
    [InlineData("http://mlflow:5000/")]
    [InlineData("http://ml-inference:8000/")]
    [InlineData("http://employee-service:8080/api/internal/x")]
    [InlineData("http://GOVERNANCE-SERVICE:8080/api/internal/x")]
    [InlineData("http://some-new-service:8080/")]
    [InlineData("http://postgres.hr360-net:5432/")]
    [InlineData("http://keycloak.:8080/")]
    [InlineData("http://hr360-postgres-1:5432/")]
    [InlineData("http://grafana:3000/")]
    [InlineData("http://prometheus:9090/")]
    [InlineData("http://mailpit:8025/")]
    [InlineData("http://governance-service:8080/api/webhooks/inbox/abc/../../internal")]
    public void Blocks_loopback_metadata_and_infra(string url) => Assert.NotNull(WebhookTargetGuard.ValidateRestHook(url));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.4", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("93.184.216.34", false)]
    public void Ip_detection(string ip, bool blocked) => Assert.Equal(blocked, WebhookTargetGuard.IsBlockedRestHookIp(IPAddress.Parse(ip)));

    [Fact]
    public void Infra_list_covers_compose_services()
    {
        var compose = FindCompose();
        if (compose is null) return; // depo dışında çalıştırılırsa atla
        var names = File.ReadAllLines(compose)
            .SkipWhile(l => l.TrimEnd() != "services:").Skip(1)
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^  [a-z0-9][a-z0-9_-]*:\s*$"))
            .Select(l => l.Trim().TrimEnd(':'))
            .ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True(WebhookTargetGuard.IsBlockedRestHookHost(n), $"docker-compose servisi engel listesinde yok: {n}"));
    }

    private static string? FindCompose()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "docker-compose.yml");
            if (File.Exists(f)) return f;
        }
        return null;
    }
}
