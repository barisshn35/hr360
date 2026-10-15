using System.Text;
using TenantService.Security;
using Xunit;

namespace Tenant.Tests;

/// <summary>Guvenlik dalgasi 2A: iki adimli dogrulama politikasi kurallari.</summary>
public class MfaPolicyRulesTests
{
    [Theory]
    [InlineData(null, "off")]
    [InlineData("", "off")]
    [InlineData("bilinmeyen", "off")]
    [InlineData("PRIVILEGED", "privileged")]
    [InlineData(" all ", "all")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, MfaPolicyRules.Normalize(input));

    [Fact]
    public void Off_never_requires() =>
        Assert.False(MfaPolicyRules.Requires("off", new[] { "tenant-admin", "hr-admin" }));

    [Theory]
    [InlineData("hr-admin", true)]
    [InlineData("tenant-admin", true)]
    [InlineData("manager", true)]
    [InlineData("employee", false)]
    [InlineData("accounting", false)]
    public void Privileged_requires_only_privileged_roles(string role, bool expected) =>
        Assert.Equal(expected, MfaPolicyRules.Requires("privileged", new[] { role }));

    [Fact]
    public void All_requires_everyone() => Assert.True(MfaPolicyRules.Requires("all", Array.Empty<string>()));

    [Fact]
    public void Passkey_or_otp_counts_as_second_factor()
    {
        Assert.True(MfaPolicyRules.HasSecondFactor(new[] { "password", "otp" }));
        Assert.True(MfaPolicyRules.HasSecondFactor(new[] { "password", "webauthn" }));
        Assert.True(MfaPolicyRules.HasSecondFactor(new[] { "webauthn-passwordless" }));
        Assert.False(MfaPolicyRules.HasSecondFactor(new[] { "password" }));
    }

    [Fact]
    public void Needs_setup_only_when_required_without_factor_and_not_pending()
    {
        Assert.True(MfaPolicyRules.NeedsSetup(true, new[] { "password" }, Array.Empty<string>()));
        Assert.False(MfaPolicyRules.NeedsSetup(false, new[] { "password" }, Array.Empty<string>()));
        Assert.False(MfaPolicyRules.NeedsSetup(true, new[] { "password", "otp" }, Array.Empty<string>()));
        Assert.False(MfaPolicyRules.NeedsSetup(true, new[] { "password", "webauthn" }, Array.Empty<string>()));
        Assert.False(MfaPolicyRules.NeedsSetup(true, new[] { "password" }, new[] { "CONFIGURE_TOTP" }));
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("privileged", true)]
    [InlineData("all", true)]
    [InlineData("ALL", false)]
    [InlineData("", false)]
    public void IsValid(string input, bool expected) => Assert.Equal(expected, MfaPolicyRules.IsValid(input));
}

/// <summary>Guvenlik dalgasi 2A: supheli giris tespiti kurallari.</summary>
public class LoginWatchRulesTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-anahtar");

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("::ffff:198.51.100.9", "198.51.100.0/24")]
    [InlineData("2001:db8:abcd:12:1::5", "2001:db8:abcd::/48")]
    [InlineData("bozuk", null)]
    [InlineData(null, null)]
    public void Network_prefix(string? ip, string? expected) => Assert.Equal(expected, LoginWatchRules.NetworkPrefix(ip));

    [Fact]
    public void Same_subnet_same_hash_different_subnet_different_hash()
    {
        var a = LoginWatchRules.NetworkHash("203.0.113.5", Key);
        var b = LoginWatchRules.NetworkHash("203.0.113.250", Key);
        var c = LoginWatchRules.NetworkHash("203.0.114.5", Key);
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(32, a!.Length);
        // Ham IP/onek ozette gorunmez; anahtar degisirse ozet degisir (veritabanindan geri cozulemez).
        Assert.DoesNotContain("203", a);
        Assert.NotEqual(a, LoginWatchRules.NetworkHash("203.0.113.5", Encoding.UTF8.GetBytes("baska")));
    }

    [Fact]
    public void First_observation_is_baseline_not_alert()
    {
        Assert.False(LoginWatchRules.IsNewNetworkAlert(knownNetworks: 0, networkKnown: false));
        Assert.False(LoginWatchRules.IsNewNetworkAlert(knownNetworks: 2, networkKnown: true));
        Assert.True(LoginWatchRules.IsNewNetworkAlert(knownNetworks: 1, networkKnown: false));
    }

    [Theory]
    [InlineData("LOGIN_ERROR", "invalid_user_credentials", true)]
    [InlineData("LOGIN_ERROR", "user_not_found", true)]
    [InlineData("LOGIN_ERROR", "expired_code", false)]
    [InlineData("LOGIN_ERROR", null, false)]
    [InlineData("LOGIN", null, false)]
    public void Counted_failures(string type, string? error, bool expected) =>
        Assert.Equal(expected, LoginWatchRules.IsCountedFailure(type, error));

    [Fact]
    public void Five_failures_in_ten_minutes_alert_once_per_hour()
    {
        var c = new FailureCounter(LoginWatchRules.FailureThreshold, LoginWatchRules.FailureWindow, LoginWatchRules.AlertCooldown);
        var t0 = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        var alerts = 0;
        for (var i = 0; i < 4; i++) if (c.ShouldAlert("u:1", c.Add("u:1", t0.AddMinutes(i)), t0.AddMinutes(i))) alerts++;
        Assert.Equal(0, alerts);
        var n = c.Add("u:1", t0.AddMinutes(4));
        Assert.Equal(5, n);
        Assert.True(c.ShouldAlert("u:1", n, t0.AddMinutes(4)));
        // Ayni saat icinde yeni hatalar tekrar uyari uretmez.
        Assert.False(c.ShouldAlert("u:1", c.Add("u:1", t0.AddMinutes(5)), t0.AddMinutes(5)));
        // Bir saat sonra yeni bir dalga tekrar uyarir.
        var t1 = t0.AddMinutes(70);
        var last = 0;
        for (var i = 0; i < 5; i++) last = c.Add("u:1", t1.AddSeconds(i));
        Assert.True(c.ShouldAlert("u:1", last, t1.AddSeconds(5)));
    }

    [Fact]
    public void Failures_spread_over_window_do_not_alert()
    {
        var c = new FailureCounter(5, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60));
        var t0 = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        var max = 0;
        for (var i = 0; i < 10; i++) max = Math.Max(max, c.Add("n:x", t0.AddMinutes(i * 3)));
        Assert.True(max < 5);
    }

    [Fact]
    public void Prune_forgets_old_keys()
    {
        var c = new FailureCounter(5, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60));
        var t0 = DateTimeOffset.UtcNow;
        c.Add("a", t0);
        c.Prune(t0.AddMinutes(30));
        Assert.Equal(0, c.TrackedKeys);
    }
}

/// <summary>Guvenlik dalgasi 2A: platform yoneticisi sureli erisim izni dogrulamasi.</summary>
public class PlatformAccessRulesTests
{
    [Fact]
    public void Valid_request() => Assert.Null(PlatformAccessRules.Validate("demo", "Destek talebi #1234 inceleme", 2));

    [Theory]
    [InlineData("", "Destek talebi #1234 inceleme", 2)]
    [InlineData("demo", "kisa", 2)]
    [InlineData("demo", "Destek talebi #1234 inceleme", 0)]
    [InlineData("demo", "Destek talebi #1234 inceleme", 5)]
    public void Invalid_requests(string slug, string reason, int hours) =>
        Assert.NotNull(PlatformAccessRules.Validate(slug, reason, hours));

    [Fact]
    public void Reason_too_long() => Assert.NotNull(PlatformAccessRules.Validate("demo", new string('x', 501), 1));
}

/// <summary>Dalga 10 (madde 57): nesne deposu imha doğrulaması — yalnızca kişisel dosya kovaları.</summary>
public class ObjectStorageEraserTests
{
    private static readonly string[] Allowed = { "hr360-documents", "cv" };

    [Theory]
    [InlineData("ozgecmis/ayse.pdf", "hr360-documents", "ozgecmis/ayse.pdf")]
    [InlineData("cv/ayse.pdf", "cv", "ayse.pdf")]
    [InlineData("s3://cv/x.pdf", "cv", "x.pdf")]
    [InlineData("/hr360-documents/a/b.pdf", "hr360-documents", "a/b.pdf")]
    public void Izinli_kova(string raw, string bucket, string key)
    {
        var (b, k) = TenantService.Services.ObjectStorageEraser.Resolve(raw, Allowed, "hr360-documents");
        Assert.Equal(bucket, b);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("tenant-logos/abc.png")]
    [InlineData("hr360-mlflow-artifacts/1/model.pkl")]
    [InlineData("../etc/passwd")]
    [InlineData("")]
    public void Izinsiz_ya_da_gecersiz(string raw) =>
        Assert.Null(TenantService.Services.ObjectStorageEraser.Resolve(raw, Allowed, "hr360-documents").Bucket);

    [Fact]
    public void Kova_listesi_varsayilani()
    {
        Assert.Equal(new[] { "hr360-documents" }, TenantService.Services.ObjectStorageEraser.Buckets(null));
        Assert.Equal(new[] { "a-b", "cvs" }, TenantService.Services.ObjectStorageEraser.Buckets(" a-b, cvs ,x"));
    }
}
