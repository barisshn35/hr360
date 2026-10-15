using TenantService.Models;
using TenantService.Services;
using Xunit;

namespace Tenant.Tests;

/// <summary>Süreli parola linkleri (E-posta C): token, özet, durum, yöntem seçimi ve SMTP kipleri.</summary>
public class PasswordLinkTests
{
    [Fact]
    public void Token_32_bayt_base64url_ve_her_seferinde_farkli()
    {
        var a = PasswordLinkService.NewToken();
        var b = PasswordLinkService.NewToken();
        Assert.Equal(43, a.Length);
        Assert.NotEqual(a, b);
        Assert.True(PasswordLinkService.LooksLikeToken(a));
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kisa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Bicimsiz_token_veritabanina_sorulmaz(string? token) => Assert.False(PasswordLinkService.LooksLikeToken(token));

    [Fact]
    public void Ozet_sha256_hex_ve_tokeni_icermez()
    {
        var t = PasswordLinkService.NewToken();
        var h = PasswordLinkService.Hash(t);
        Assert.Equal(64, h.Length);
        Assert.Matches("^[0-9a-f]{64}$", h);
        Assert.Equal(h, PasswordLinkService.Hash(t));
        Assert.DoesNotContain(t, h);
    }

    [Fact]
    public void Link_adresi_parola_belirle_sayfasi()
    {
        Environment.SetEnvironmentVariable("PUBLIC_ORIGIN", "https://hr.example.com/");
        Assert.Equal("https://hr.example.com/parola-belirle/abc", PasswordLinkService.LinkFor("abc"));
    }

    private static PasswordInvite Invite(Action<PasswordInvite>? edit = null)
    {
        var i = new PasswordInvite
        {
            KeycloakUserId = "u", Email = "ayse@sirket.com", Purpose = PasswordInvitePurposes.Setup,
            Channel = PasswordInviteChannels.Link, TokenHash = new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(72),
        };
        edit?.Invoke(i);
        return i;
    }

    [Fact]
    public void Durumlar()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("active", Invite().StatusAt(now));
        Assert.Equal("used", Invite(i => i.UsedAt = now).StatusAt(now));
        Assert.Equal("revoked", Invite(i => i.RevokedAt = now).StatusAt(now));
        Assert.Equal("expired", Invite(i => i.ExpiresAt = now.AddSeconds(-1)).StatusAt(now));
        // Kullanılmış link süresi dolsa da "kullanıldı" görünür.
        Assert.Equal("used", Invite(i => { i.UsedAt = now; i.ExpiresAt = now.AddDays(-1); }).StatusAt(now));
        // Dizin sağlamasında e-posta yokken link üretilmez: yönetici yeniden üretene kadar "bekliyor".
        Assert.Equal("pending", Invite(i => { i.TokenHash = null; i.Channel = PasswordInviteChannels.Email; }).StatusAt(now));
        Assert.Equal("active", Invite(i => { i.TokenHash = null; i.Channel = PasswordInviteChannels.Keycloak; }).StatusAt(now));
    }

    [Theory]
    [InlineData(null, "invite", true, "invite", "invite")]
    [InlineData(null, "keycloak", true, "keycloak", "keycloak")]
    [InlineData(null, "keycloak", false, "keycloak", "invite")]   // SMTP yok: eski yöntem davet linkine düşer
    [InlineData("keycloak", "invite", true, "keycloak", "keycloak")]
    [InlineData("keycloak", "invite", false, "keycloak", "invite")]
    [InlineData("invite", "keycloak", true, "invite", "invite")]
    [InlineData("bilinmeyen", "bozuk", true, "invite", "invite")]
    public void Yontem_secimi(string? tenant, string platform, bool smtp, string selected, string effective)
        => Assert.Equal((selected, effective), PlatformSettingsService.ResolveMethod(tenant, platform, smtp));

    [Fact]
    public void Smtp_kipleri()
    {
        Environment.SetEnvironmentVariable("SMTP_HOST", "mailpit");
        Assert.Equal("mailpit", PlatformSettingsService.FromRow(new PlatformSetting()).Mode);
        Assert.False(PlatformSettingsService.FromRow(new PlatformSetting()).Configured);

        Environment.SetEnvironmentVariable("SMTP_HOST", "smtp.sirket.com");
        Environment.SetEnvironmentVariable("SMTP_PORT", "465");
        var env = PlatformSettingsService.FromRow(new PlatformSetting());
        Assert.Equal(("env", "smtp.sirket.com", 465, true), (env.Mode, env.Host, env.Port, env.Configured));

        // install.sh --mail-mode=self: paketteki posta servisi (mailer:25), gerçek gönderim sayılır.
        Environment.SetEnvironmentVariable("SMTP_HOST", "mailer");
        Environment.SetEnvironmentVariable("SMTP_PORT", "25");
        var self = PlatformSettingsService.FromRow(new PlatformSetting());
        Assert.Equal(("self", "mailer", 25, true), (self.Mode, self.Host, self.Port, self.Configured));
        var realm = PlatformSettingsService.RealmSmtp(self, _ => "");
        Assert.Equal(("false", "false", "false"), (realm["auth"], realm["ssl"], realm["starttls"]));
        Environment.SetEnvironmentVariable("SMTP_HOST", "smtp.sirket.com");
        Environment.SetEnvironmentVariable("SMTP_PORT", "465");

        // Arayüzden "test kutusu" seçildiyse .env'deki gerçek sunucu kullanılmaz.
        Assert.Equal("mailpit", PlatformSettingsService.FromRow(new PlatformSetting { SmtpMode = "mailpit" }).Mode);

        var custom = PlatformSettingsService.FromRow(new PlatformSetting
        {
            SmtpMode = "custom", SmtpHost = "smtp.office365.com", SmtpPort = 587, SmtpUser = "ik@sirket.com",
            SmtpPasswordEncrypted = "enc", SmtpFromAddress = "ik@sirket.com", SmtpFromName = "İK",
        });
        Assert.Equal(("custom", "smtp.office365.com", 587, true, "İK"), (custom.Mode, custom.Host, custom.Port, custom.Configured, custom.FromName));
        Environment.SetEnvironmentVariable("SMTP_HOST", null);
        Environment.SetEnvironmentVariable("SMTP_PORT", null);
    }

    [Fact]
    public void Realm_smtp_ayari()
    {
        var s = new PlatformSettingsService.EffectiveSmtp("custom", "smtp.sirket.com", 587, "ik", "enc", null, "ik@sirket.com", "HR360");
        var d = PlatformSettingsService.RealmSmtp(s, _ => "gizli");
        Assert.Equal("true", d["auth"]);
        Assert.Equal("true", d["starttls"]);
        Assert.Equal("gizli", d["password"]);
        var m = PlatformSettingsService.RealmSmtp(new("mailpit", "mailpit", 1025, null, null, null, "noreply@hr360.local", "HR360"), _ => "");
        Assert.Equal("false", m["auth"]);
        Assert.False(m.ContainsKey("password"));
    }

    [Theory]
    [InlineData("ayse.yilmaz@sirket.com", "ay***@sirket.com")]
    [InlineData("a@b.co", "a***@b.co")]
    [InlineData("bozuk", "***")]
    public void Eposta_maskeleme(string email, string expected) => Assert.Equal(expected, PasswordLinkService.MaskEmail(email));

    [Fact]
    public void Eposta_metinleri_linki_govdeye_yazmaz()
    {
        var (subject, body, label) = PasswordLinkService.EmailTexts(PasswordInvitePurposes.Setup, "tr", "Acme", "Ayşe Yılmaz", DateTimeOffset.UtcNow.AddHours(72));
        Assert.Contains("Acme", subject);
        Assert.Contains("Ayşe Yılmaz", body);
        Assert.DoesNotContain("parola-belirle", body);
        Assert.Equal("Parolamı belirle", label);
        var en = PasswordLinkService.EmailTexts(PasswordInvitePurposes.Reset, "en", null, null, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("Reset your HR360 password", en.Subject);
    }

    [Theory]
    [InlineData("invalidPasswordMinLengthMessage", "Parola çok kısa. Daha uzun bir parola seçin.")]
    [InlineData("invalidPasswordHistoryMessage", "Bu parolayı yakın zamanda kullandınız; farklı bir parola seçin.")]
    [InlineData("baskaBirKod", "Parola, parola kurallarına uymuyor.")]
    public void Politika_iletileri(string code, string expected)
        => Assert.Equal(expected, TenantService.Controllers.PublicPasswordController.PolicyMessage(code, "desc"));
}
