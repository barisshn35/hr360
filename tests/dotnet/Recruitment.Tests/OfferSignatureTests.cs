using RecruitmentService.Models;
using RecruitmentService.Services;
using Xunit;

namespace Recruitment.Tests;

/// <summary>Dalga 11 (madde 76): teklif mektubu e-imzası — saf kurallar ve governance istemcisi hata eşlemesi.</summary>
public class OfferSignatureTests
{
    private static Offer NewOffer(OfferStatus status = OfferStatus.Sent, int expiresInDays = 5) => new()
    {
        PositionTitle = "Backend <Geliştirici>",
        GrossSalary = 100,
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
        ExpiresAt = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(expiresInDays),
        Status = status,
        SalaryLetterText = "Sayın Aday,\r\nÜcret: 100 TL <script>alert(1)</script>",
    };

    [Fact]
    public void Mektup_ozeti_satir_sonundan_bagimsiz_ve_64_hex()
    {
        var a = OfferSignature.LetterHash("a\r\nb");
        Assert.Equal(a, OfferSignature.LetterHash("a\nb"));
        Assert.Equal(64, a.Length);
        Assert.All(a, ch => Assert.True(char.IsAsciiHexDigitLower(ch) || char.IsAsciiDigit(ch)));
        Assert.NotEqual(a, OfferSignature.LetterHash("a\nc"));
    }

    [Fact]
    public void Yalnizca_gonderilmis_suresi_dolmamis_imzasiz_teklif_imzalanir()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Null(OfferSignature.CanSign(NewOffer(), today));
        Assert.NotNull(OfferSignature.CanSign(NewOffer(OfferStatus.Approved), today));
        Assert.NotNull(OfferSignature.CanSign(NewOffer(OfferStatus.Accepted), today));
        Assert.NotNull(OfferSignature.CanSign(NewOffer(OfferStatus.Withdrawn), today));
        Assert.Equal("Teklifin geçerlilik süresi dolmuş", OfferSignature.CanSign(NewOffer(expiresInDays: -1), today));
        var signed = NewOffer();
        signed.SignatureEvidenceId = Guid.NewGuid();
        Assert.Equal("Teklif zaten imzalanmış", OfferSignature.CanSign(signed, today));
    }

    [Theory]
    [InlineData("ayse@example.com", "a***@example.com")]
    [InlineData("ab@x.io", "a**@x.io")]
    [InlineData("gecersiz", "***")]
    [InlineData(null, "")]
    public void Eposta_maskelenir(string? email, string expected) => Assert.Equal(expected, OfferSignature.MaskEmail(email));

    [Fact]
    public void Kanit_basligi_aday_adi_ve_ucret_icermez()
    {
        var o = NewOffer();
        Assert.Equal("İş teklifi: Backend <Geliştirici>", OfferSignature.Title(o));
        Assert.DoesNotContain("100", OfferSignature.Title(o));
    }

    [Fact]
    public void Imzali_belge_kodlanir_ve_kaniti_icerir()
    {
        var o = NewOffer();
        var ev = Guid.NewGuid();
        var html = OfferSignature.RenderSignedHtml("Acme & Co", o, new string('a', 64), ev, new string('b', 64),
            new DateTimeOffset(2026, 10, 7, 9, 30, 0, TimeSpan.Zero), "OTP-Email", "Basit elektronik imza");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Acme &amp; Co", html);
        Assert.Contains(ev.ToString(), html);
        Assert.Contains(new string('b', 64), html);
        Assert.Contains("07.10.2026 12:30:00", html); // Türkiye saati (UTC+3)
        Assert.Equal(OfferSignature.Sha256Hex(html), OfferSignature.Sha256Hex(html));
    }

    [Fact]
    public void Governance_hata_govdesi_eslenir()
    {
        var r = GovernanceSignatureClient.ParseError<GovOtp>(400, "{\"message\":\"Kod hatalı\",\"code\":\"otp_invalid\",\"attemptsLeft\":3}");
        Assert.False(r.Ok);
        Assert.Equal(("otp_invalid", 3, 400), (r.Code, r.AttemptsLeft, r.Status));
        // Gövdesiz 404: iç uç kapalı (anahtar yanlış/yok) → servis erişilemez sayılır.
        var closed = GovernanceSignatureClient.ParseError<GovOtp>(404, "");
        Assert.Equal((503, GovernanceSignatureClient.Unavailable), (closed.Status, closed.Code));
    }
}
