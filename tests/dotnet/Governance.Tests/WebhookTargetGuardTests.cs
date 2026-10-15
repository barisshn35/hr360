using System.Net;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>İK webhook hedefi SSRF denetimi: yalnızca https ve iç ağ dışı adresler.</summary>
public class WebhookTargetGuardTests
{
    [Theory]
    [InlineData("https://hooks.example.com/x")]
    [InlineData("https://hooks.zapier.com/hooks/catch/1/a")]
    [InlineData("http://governance-service:8080/api/webhooks/inbox/abc123")]
    public void Accepts_external_https_and_self_inbox(string url) => Assert.Null(WebhookTargetGuard.Validate(url));

    [Theory]
    [InlineData("http://hooks.example.com/x")]
    [InlineData("ftp://hooks.example.com/x")]
    [InlineData("https://localhost/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://192.168.1.1/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://postgres:5432/")]
    [InlineData("https://chatmock:8000/hooks/x")]
    [InlineData("http://governance-service:8080/api/webhooks/inbox/abc/../../internal")]
    [InlineData("gecersiz")]
    public void Rejects_http_and_internal(string url) => Assert.NotNull(WebhookTargetGuard.Validate(url));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.4", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::ffff:192.168.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:4700::1111", false)]
    public void Private_ip_detection(string ip, bool expected) => Assert.Equal(expected, WebhookTargetGuard.IsPrivate(IPAddress.Parse(ip)));
}
