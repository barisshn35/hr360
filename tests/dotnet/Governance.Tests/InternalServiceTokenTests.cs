using GovernanceService.Security;
using Xunit;

namespace Governance.Tests;

/// <summary>
/// İç servis anahtarı (X-Internal-Token) denetimi ve kesintisiz değiştirme penceresi
/// (INTERNAL_SERVICE_TOKEN_PREVIOUS). Ortam değişkenleri süreç genelinde olduğundan
/// bu anahtara dokunan sınıflar aynı koleksiyonda (sıralı) koşar.
/// </summary>
[Collection("InternalServiceToken")]
public class InternalServiceTokenTests
{
    [Fact]
    public void Saf_karsilastirma()
    {
        Assert.True(InternalServiceToken.Matches("yeni", "yeni", null));
        Assert.True(InternalServiceToken.Matches("eski", "yeni", "eski"));
        Assert.True(InternalServiceToken.Matches("yeni", "yeni", "eski"));
        Assert.False(InternalServiceToken.Matches("baska", "yeni", "eski"));
        Assert.False(InternalServiceToken.Matches("yeni-uzun-deger", "yeni", null));
        Assert.False(InternalServiceToken.Matches(null, "yeni", null));
        Assert.False(InternalServiceToken.Matches("", "yeni", ""));
        // Önceki anahtar boşsa boş gelen anahtar kabul edilmez.
        Assert.False(InternalServiceToken.Matches("", "yeni", null));
        // Geçerli anahtar yoksa uç kapalı: önceki anahtar tek başına yetmez.
        Assert.False(InternalServiceToken.Matches("eski", null, "eski"));
        Assert.False(InternalServiceToken.Matches("eski", "", "eski"));
        Assert.False(InternalServiceToken.Matches("", "", ""));
    }

    [Fact]
    public void Ortamdan_okur_ve_pencere_kapaninca_eski_reddedilir()
    {
        var cur = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        var prev = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS");
        try
        {
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", "yeni-anahtar");
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", "eski-anahtar");
            Assert.True(InternalServiceToken.Matches("yeni-anahtar"));
            Assert.True(InternalServiceToken.Matches("eski-anahtar"));
            Assert.False(InternalServiceToken.Matches("x"));

            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", null);
            Assert.True(InternalServiceToken.Matches("yeni-anahtar"));
            Assert.False(InternalServiceToken.Matches("eski-anahtar"));

            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null);
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", "eski-anahtar");
            Assert.False(InternalServiceToken.Matches("eski-anahtar"));
            Assert.False(InternalServiceToken.Matches(""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", cur);
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", prev);
        }
    }

    [Fact]
    public void Imza_ic_ucu_onceki_anahtari_pencerede_kabul_eder()
    {
        var cur = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        var prev = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS");
        try
        {
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", "yeni-anahtar");
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", "eski-anahtar");
            Assert.True(GovernanceService.Controllers.InternalSignaturesController.TokenOk("eski-anahtar"));
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", "");
            Assert.False(GovernanceService.Controllers.InternalSignaturesController.TokenOk("eski-anahtar"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", cur);
            Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN_PREVIOUS", prev);
        }
    }
}
