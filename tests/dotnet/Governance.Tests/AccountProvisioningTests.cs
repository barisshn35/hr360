using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GovernanceService.Infrastructure.Provisioning;
using Xunit;

namespace Governance.Tests;

/// <summary>Dalga 12 (madde 92): hesap açma/kapatma — ad → adres, alan adı denetimi, geçici parola, servis hesabı JWT'si, dört göz.</summary>
public class AccountProvisioningTests
{
    [Theory]
    [InlineData("Ayşe Nur", "Yılmaz", "Sirket.com", "aysenur.yilmaz@sirket.com")]
    [InlineData("Şükrü", "Öztürk", "sirket.com", "sukru.ozturk@sirket.com")]
    [InlineData("İsmail", "Çağlar", "sirket.com.tr", "ismail.caglar@sirket.com.tr")]
    [InlineData("Ömer", "Güneş-Işık", "x.io", "omer.gunesisik@x.io")]
    [InlineData("José", "Peña", "x.io", "jose.pena@x.io")]
    [InlineData("", "Kaya", "x.io", "kaya@x.io")]
    public void Ad_soyaddan_is_adresi_onerisi(string first, string last, string domain, string expected) =>
        Assert.Equal(expected, AccountNaming.Propose(first, last, domain));

    [Fact]
    public void Harf_icermeyen_adla_oneri_yok() => Assert.Null(AccountNaming.Propose("—", "…", "x.io"));

    [Theory]
    [InlineData("ayse.yilmaz@sirket.com", "sirket.com", true)]
    [InlineData("AYSE.YILMAZ@SIRKET.COM", "sirket.com", true)]
    [InlineData("ayse.yilmaz@baska.com", "sirket.com", false)]
    [InlineData("ayse..yilmaz@sirket.com", "sirket.com", false)]
    [InlineData(".ayse@sirket.com", "sirket.com", false)]
    [InlineData("ayşe@sirket.com", "sirket.com", false)]
    [InlineData("ayse@sub.sirket.com", "sirket.com", false)]
    [InlineData("", "sirket.com", false)]
    public void Hesap_adresi_yalnizca_yapilandirilmis_alan_adinda(string email, string domain, bool ok) =>
        Assert.Equal(ok, AccountNaming.ValidAccountEmail(email, domain));

    [Theory]
    [InlineData("sirket.com", true)]
    [InlineData("prov-test.hr360.example", true)]
    [InlineData("localhost", false)]
    [InlineData("-x.com", false)]
    [InlineData("a b.com", false)]
    [InlineData(null, false)]
    public void Alan_adi_dogrulama(string? d, bool ok) => Assert.Equal(ok, AccountNaming.ValidDomain(d));

    [Fact]
    public void Gecici_parola_guclu_ve_her_seferinde_farkli()
    {
        var all = Enumerable.Range(0, 50).Select(_ => AccountNaming.TemporaryPassword()).ToList();
        Assert.Equal(50, all.Distinct().Count());
        Assert.All(all, p =>
        {
            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsAsciiLetterUpper);
            Assert.Contains(p, char.IsAsciiLetterLower);
            Assert.Contains(p, char.IsAsciiDigit);
            Assert.Contains(p, c => "!#$%*+-=?@".Contains(c));
        });
    }

    [Theory]
    [InlineData("google", "Google")]
    [InlineData(" MICROSOFT ", "Microsoft")]
    [InlineData("zoom", null)]
    [InlineData(null, null)]
    public void Saglayici_adi(string? input, string? expected) => Assert.Equal(expected, AccountNaming.NormalizeProvider(input));

    private static (string Json, RSA Rsa) ServiceAccount(string type = "service_account")
    {
        var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new
        {
            type, project_id = "p", private_key_id = "k1", private_key = rsa.ExportPkcs8PrivateKeyPem(),
            client_email = "hr360@p.iam.gserviceaccount.com", token_uri = "https://oauth2.googleapis.com/token",
        });
        return (json, rsa);
    }

    [Fact]
    public void Servis_hesabi_anahtari_okunur()
    {
        var (json, rsa) = ServiceAccount();
        using var _ = rsa;
        var (email, key, error) = AccountNaming.ParseServiceAccount(json);
        Assert.Null(error);
        Assert.Equal("hr360@p.iam.gserviceaccount.com", email);
        Assert.Contains("PRIVATE KEY", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{bozuk")]
    [InlineData("[1,2]")]
    [InlineData("""{"type":"authorized_user","client_email":"a@b","private_key":"x"}""")]
    [InlineData("""{"type":"service_account","client_email":"a@b","private_key":"-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----"}""")]
    public void Gecersiz_servis_hesabi_anahtari_reddedilir(string json)
    {
        var (email, key, error) = AccountNaming.ParseServiceAccount(json);
        Assert.NotNull(error);
        Assert.Null(email);
        Assert.Null(key);
    }

    [Fact]
    public void Servis_hesabi_JWTsi_RS256_ile_imzali_ve_alan_genelinde_yetki_icin_sub_tasir()
    {
        var (json, rsa) = ServiceAccount();
        using var _ = rsa;
        var (email, key, _) = AccountNaming.ParseServiceAccount(json);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = AccountNaming.ServiceAccountAssertion(email!, key!, "admin@sirket.com", GoogleDirectoryProvisioner.Scope, "https://oauth2.googleapis.com/token", now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] D(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), D(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var header = JsonDocument.Parse(D(parts[0])).RootElement;
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        var c = JsonDocument.Parse(D(parts[1])).RootElement;
        Assert.Equal("hr360@p.iam.gserviceaccount.com", c.GetProperty("iss").GetString());
        Assert.Equal("admin@sirket.com", c.GetProperty("sub").GetString());
        Assert.Equal("https://www.googleapis.com/auth/admin.directory.user", c.GetProperty("scope").GetString());
        Assert.Equal(1_800_000_000, c.GetProperty("iat").GetInt64());
        Assert.Equal(1_800_003_600, c.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void Dort_goz_ve_kendi_hesabi_kurali()
    {
        var subject = Guid.NewGuid();
        // Kimse kendi hesabıyla ilgili isteğe karar veremez.
        Assert.NotNull(AccountNaming.DecisionBlock("Auto", null, "u1", subject, subject));
        // Elle açılan isteği açan kişi onaylayamaz.
        Assert.NotNull(AccountNaming.DecisionBlock("Manual", "u1", "u1", subject, Guid.NewGuid()));
        Assert.Null(AccountNaming.DecisionBlock("Manual", "u1", "u2", subject, Guid.NewGuid()));
        // Otomatik istek: herhangi bir İK yöneticisi (çalışan kaydı olmasa da) onaylayabilir.
        Assert.Null(AccountNaming.DecisionBlock("Auto", null, "u1", subject, null));
    }
}
