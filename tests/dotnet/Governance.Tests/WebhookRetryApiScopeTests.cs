using GovernanceService.Infrastructure;
using GovernanceService.Models;
using Xunit;

namespace Governance.Tests;

/// <summary>Dalga 12: webhook yeniden deneme (geri çekilme) ve API anahtarı yetki eşleştirmesi.</summary>
public class WebhookRetryApiScopeTests
{
    [Theory]
    [InlineData(null, "Connection refused", true)]
    [InlineData(500, "HTTP 500", true)]
    [InlineData(503, "HTTP 503", true)]
    [InlineData(429, "HTTP 429", true)]
    [InlineData(408, "HTTP 408", true)]
    [InlineData(400, "HTTP 400", false)]
    [InlineData(404, "HTTP 404", false)]
    [InlineData(410, "HTTP 410", false)]
    [InlineData(null, "transfer_basis_required", false)]
    [InlineData(200, null, false)]
    public void Gecici_hatalar_yeniden_denenir(int? status, string? error, bool expected) =>
        Assert.Equal(expected, WebhookRetry.IsRetryable(status, error));

    [Fact]
    public void Geri_cekilme_ustel_ve_sinirli()
    {
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(t0.AddSeconds(60), WebhookRetry.NextRetryAt(1, t0, 60));
        Assert.Equal(t0.AddMinutes(5), WebhookRetry.NextRetryAt(2, t0, 60));
        Assert.Equal(t0.AddMinutes(30), WebhookRetry.NextRetryAt(3, t0, 60));
        Assert.Equal(t0.AddHours(2), WebhookRetry.NextRetryAt(4, t0, 60));
        Assert.Equal(t0.AddHours(12), WebhookRetry.NextRetryAt(5, t0, 60));
        Assert.Null(WebhookRetry.NextRetryAt(6, t0, 60)); // 6. deneme sonuncu
        Assert.Null(WebhookRetry.NextRetryAt(0, t0, 60));
        Assert.Equal(new[] { 2, 10, 60, 240, 1440 }, WebhookRetry.ScheduleSeconds(2));
    }

    [Fact]
    public void Planlama_durumlari()
    {
        var hook = new Webhook { IsEnabled = true };
        var failed = new WebhookDelivery { EventId = Guid.NewGuid(), StatusCode = 502, Error = "HTTP 502", Attempt = 1 };
        WebhookRetry.Schedule(failed, hook, "leave.approved");
        Assert.Equal("pending", failed.RetryState);
        Assert.NotNull(failed.NextRetryAt);

        var ping = new WebhookDelivery { EventId = Guid.NewGuid(), StatusCode = 502, Error = "HTTP 502" };
        WebhookRetry.Schedule(ping, hook, "ping");
        Assert.Null(ping.RetryState);

        var last = new WebhookDelivery { EventId = Guid.NewGuid(), StatusCode = 500, Error = "HTTP 500", Attempt = WebhookRetry.MaxAttempts };
        WebhookRetry.Schedule(last, hook, "leave.approved");
        Assert.Equal("gave_up", last.RetryState);
        Assert.Null(last.NextRetryAt);

        var disabled = new WebhookDelivery { EventId = Guid.NewGuid(), Error = "timeout" };
        WebhookRetry.Schedule(disabled, new Webhook { IsEnabled = false }, "leave.approved");
        Assert.Equal("gave_up", disabled.RetryState);

        var permanent = new WebhookDelivery { EventId = Guid.NewGuid(), StatusCode = 404, Error = "HTTP 404" };
        WebhookRetry.Schedule(permanent, hook, "leave.approved");
        Assert.Null(permanent.RetryState);

        var ok = new WebhookDelivery { EventId = Guid.NewGuid(), StatusCode = 200 };
        WebhookRetry.Schedule(ok, hook, "leave.approved");
        Assert.Null(ok.RetryState);
    }

    [Fact]
    public void Yetkiler_normallestirilir()
    {
        Assert.Equal(new[] { "employees:read", "leaves:read" }, ApiScopes.Normalize(new[] { "leave:read", "EMPLOYEE:READ", "leaves:read", "bilinmeyen" }));
        Assert.Equal(new[] { "read-only" }, ApiScopes.Normalize(new[] { "read:*" }));
        Assert.Empty(ApiScopes.Normalize(null));
    }

    [Theory]
    [InlineData(new[] { "read-only" }, "employees:read", true)]
    [InlineData(new[] { "read-only" }, "leaves:read", true)]
    [InlineData(new[] { "read-only" }, "hooks:write", false)]
    [InlineData(new[] { "leaves:read" }, "leaves:read", true)]
    [InlineData(new[] { "leave:read" }, "leaves:read", true)]
    [InlineData(new[] { "leaves:read" }, "employees:read", false)]
    [InlineData(new[] { "hooks:write" }, "events:read", false)]
    public void Yetki_eslestirme(string[] granted, string required, bool expected) =>
        Assert.Equal(expected, ApiScopes.Allows(granted, required));

    [Fact]
    public void Iptal_ve_sure_dolumu()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(ApiScopes.Inactive(null, null, now));
        Assert.Null(ApiScopes.Inactive(null, now.AddDays(1), now));
        Assert.Equal("expired", ApiScopes.Inactive(null, now, now));
        Assert.Equal("revoked", ApiScopes.Inactive(now, now.AddDays(1), now));
        Assert.Null(ApiScopes.ExpiryFromDays(0, now));
        Assert.Equal(now.AddDays(90), ApiScopes.ExpiryFromDays(90, now));
        Assert.Equal(now.AddDays(730), ApiScopes.ExpiryFromDays(5000, now));
    }
}
