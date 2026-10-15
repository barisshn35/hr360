using System.Text.Json;
using GovernanceService.Controllers;
using GovernanceService.Infrastructure;
using GovernanceService.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Governance.Tests;

// INTERNAL_SERVICE_TOKEN süreç genelinde değiştirildiği için anahtar testleriyle paralel koşmaz.
/// <summary>Y28 tek imza motoru: ortak kurallar (saf), kanıt bütünlüğü ve iç uçların anahtar/tür denetimi.</summary>
[Collection("InternalServiceToken")]
public class SignatureEngineTests
{
    [Fact]
    public void Tek_kural_seti()
    {
        Assert.Equal(6, Signatures.CodeDigits);
        Assert.Equal(10, Signatures.ValidMinutes);
        Assert.Equal(5, Signatures.MaxAttempts);
        Assert.Equal(5, Signatures.MaxCodesPerHour);
        Assert.Equal(TimeSpan.FromSeconds(30), Signatures.ResendCooldown);
    }

    [Fact]
    public void Tek_uyari_metni()
    {
        Assert.Equal("Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir.", Signatures.DisclaimerTr);
        Assert.Equal("Simple electronic signature — not a qualified (secure) electronic signature under Turkish Law No. 5070.", Signatures.DisclaimerEn);
    }

    [Fact]
    public void Kod_istegi_sinirlari_saatlik_ve_bekleme()
    {
        var now = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        Assert.Null(Signatures.RateLimit(0, null, now));
        Assert.Null(Signatures.RateLimit(4, now.AddSeconds(-31), now));
        Assert.Equal("otp_cooldown", Signatures.RateLimit(1, now.AddSeconds(-29), now));
        Assert.Equal("otp_rate_limited", Signatures.RateLimit(5, now.AddMinutes(-20), now));
        // Saatlik sınır beklemeden önce gelir.
        Assert.Equal("otp_rate_limited", Signatures.RateLimit(5, now.AddSeconds(-1), now));
    }

    [Theory]
    [InlineData(null, "InApp", "OTP-InApp")]
    [InlineData("InApp", "InApp", "OTP-InApp")]
    [InlineData("Email", "Email", "OTP-Email")]
    [InlineData("InApp+Email", "InApp+Email", "OTP-InApp+Email")]
    [InlineData("Both", "InApp+Email", "OTP-InApp+Email")]
    [InlineData("Sms", "InApp", "OTP-InApp")]
    public void Kanal_ve_yontem(string? input, string channel, string method)
    {
        Assert.Equal(channel, Signatures.NormalizeChannel(input));
        Assert.Equal(method, Signatures.MethodFor(Signatures.NormalizeChannel(input)));
    }

    [Fact]
    public void Bicimi_bozuk_kod_hatali_deneme()
    {
        var id = Guid.NewGuid();
        var h = Signatures.Hash(id, "123456");
        Assert.True(Signatures.Matches(id, " 123456 ", h));
        Assert.False(Signatures.Matches(id, null, h));
        Assert.False(Signatures.Matches(id, "12345", h));
        Assert.False(Signatures.Matches(id, "12345a", h));
    }

    [Theory]
    [InlineData("HrDocument", true)]
    [InlineData("DocumentRequest", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1Doc", false)]
    [InlineData("Hr-Document", false)]
    [InlineData("Hr Document", false)]
    public void Belge_turu_adi(string? t, bool ok) => Assert.Equal(ok, Signatures.ValidDocumentType(t));

    private static SignatureEvidence Ev(string disclaimer, string method = "OTP-InApp")
    {
        var id = Guid.NewGuid(); var who = Guid.NewGuid(); var at = new DateTime(2026, 10, 11, 9, 0, 0, 123, DateTimeKind.Utc);
        var hash = Signatures.Sha256Hex(Signatures.Canonical("HrDocument", id, 2, new string('a', 64), who, at, method, "10.0.0.0/24", disclaimer));
        return new SignatureEvidence(Guid.NewGuid(), "HrDocument", id, 2, new string('a', 64), who, at, method, "10.0.0.0/24", disclaimer, hash, "Sözleşme.pdf");
    }

    [Fact]
    public void Butunluk_saklanan_uyari_metniyle_dogrulanir()
    {
        Assert.True(Ev(Signatures.DisclaimerTr).IntegrityOk);
        // Metin değişmeden önce imzalanmış kanıt (eski uyarı metni) hâlâ doğrulanır.
        Assert.True(Ev("Basit elektronik imza — 5070 sayılı Kanun kapsamında güvenli/nitelikli elektronik imza değildir").IntegrityOk);
        var e = Ev(Signatures.DisclaimerTr);
        Assert.False((e with { IpPrefix = "10.0.1.0/24" }).IntegrityOk);
        Assert.False((e with { DocumentVersion = 1 }).IntegrityOk);
        Assert.False((e with { DocumentType = "DocumentRequest" }).IntegrityOk);
        // Başlık yalnızca görüntü içindir; kanonik metne girmez.
        Assert.True((e with { Title = null }).IntegrityOk);
    }

    [Fact]
    public void Gorunum_alanlari_ve_dil()
    {
        var e = Ev(Signatures.DisclaimerTr);
        var tr = JsonSerializer.SerializeToElement(SignatureEngine.View(e, false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(tr.GetProperty("integrityOk").GetBoolean());
        Assert.Equal("HrDocument", tr.GetProperty("documentType").GetString());
        Assert.Equal("Sözleşme.pdf", tr.GetProperty("title").GetString());
        Assert.Equal(Signatures.DisclaimerTr, tr.GetProperty("disclaimer").GetString());
        var en = JsonSerializer.SerializeToElement(SignatureEngine.View(e, true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(Signatures.DisclaimerEn, en.GetProperty("disclaimer").GetString());
    }

    [Fact]
    public void Hata_govdesi_kod_ve_kalan_deneme()
    {
        var err = new SignatureError(400, "otp_invalid", "Kod hatalı. Kalan deneme: 3.", "Wrong code. Attempts left: 3.", 3);
        var j = JsonSerializer.SerializeToElement(err.Body(false));
        Assert.Equal("otp_invalid", j.GetProperty("code").GetString());
        Assert.Equal(3, j.GetProperty("attemptsLeft").GetInt32());
        Assert.StartsWith("Wrong code", JsonSerializer.SerializeToElement(err.Body(true)).GetProperty("message").GetString());
        var j2 = JsonSerializer.SerializeToElement(SignatureEngine.AlreadySigned().Body(false));
        Assert.Equal("already_signed", j2.GetProperty("code").GetString());
        Assert.False(j2.TryGetProperty("attemptsLeft", out _));
    }

    /* ------------------------------------------------------------ iç uçlar */

    private const string Token = "unit-test-internal-token";

    private static (InternalSignaturesController C, TenantContext Tenant) Internal(string? header)
    {
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);
        // Bağlantı açılmaz: anahtar/tür denetimi veritabanından önce yapılır.
        var sql = new Sql(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        var sp = new ServiceCollection().BuildServiceProvider();
        var engine = new SignatureEngine(sql, new Notifier(sql, NullLogger<Notifier>.Instance), sp);
        var tenant = new TenantContext();
        var c = new InternalSignaturesController(engine, sql, tenant);
        var ctx = new DefaultHttpContext();
        if (header is not null) ctx.Request.Headers["X-Internal-Token"] = header;
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (c, tenant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task Ic_uclar_anahtarsiz_404(string? header)
    {
        var (c, tenant) = Internal(header);
        Assert.IsType<NotFoundResult>(await c.Otp(new("demo", "HrDocument", Guid.NewGuid(), Guid.NewGuid(), "x", null, 1), default));
        Assert.IsType<NotFoundResult>(await c.Sign(new("demo", "HrDocument", Guid.NewGuid(), Guid.NewGuid(), null, "123456", new string('a', 64), 1, null, null, null, null), default));
        Assert.IsType<NotFoundResult>(await c.Evidence(new("demo", "HrDocument", Guid.NewGuid(), null), default));
        Assert.Null(tenant.TenantSlug);
    }

    [Fact]
    public async Task Ic_uclar_belge_talebi_turunu_imzalamaz()
    {
        var (c, tenant) = Internal(Token);
        var r = await c.Sign(new("demo", "DocumentRequest", Guid.NewGuid(), Guid.NewGuid(), null, "123456", new string('a', 64), 1, null, null, null, null), default);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(r).StatusCode);
        Assert.Null(tenant.TenantSlug);
    }

    [Fact]
    public async Task Ic_uclar_kiraci_ve_ozet_ister()
    {
        var (c, _) = Internal(Token);
        Assert.IsType<BadRequestObjectResult>(await c.Otp(new(" ", "HrDocument", Guid.NewGuid(), Guid.NewGuid(), "x", null, 1), default));
        var (c2, tenant) = Internal(Token);
        Assert.IsType<BadRequestObjectResult>(await c2.Sign(new("demo", "HrDocument", Guid.NewGuid(), Guid.NewGuid(), null, "123456", "kisa", 1, null, null, null, null), default));
        Assert.Equal("demo", tenant.TenantSlug);
    }

    [Fact]
    public void Anahtar_yoksa_uc_kapali()
    {
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null);
        try { Assert.False(InternalSignaturesController.TokenOk("")); }
        finally { Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token); }
        Assert.True(InternalSignaturesController.TokenOk(Token));
        Assert.False(InternalSignaturesController.TokenOk(Token + "x"));
    }

    /* ------------------------------------------------------------ dalga 11: aday (dış imzalayan) */

    [Theory]
    [InlineData("OfferLetter", "Candidate", "aday@example.com", true, null)]
    [InlineData("OfferLetter", "Candidate", null, false, null)]
    [InlineData("OfferLetter", "Candidate", null, true, "no_email")]
    [InlineData("OfferLetter", "Candidate", "gecersiz", true, "no_email")]
    [InlineData("OfferLetter", null, "aday@example.com", true, "invalid_signer")]
    [InlineData("OfferLetter", "Employee", "aday@example.com", true, "invalid_signer")]
    [InlineData("HrDocument", null, null, true, null)]
    [InlineData("HrDocument", "Employee", null, true, null)]
    [InlineData("HrDocument", "Candidate", "aday@example.com", true, "invalid_signer")]
    [InlineData("HrDocument", null, "baska@example.com", true, "invalid_signer")]
    public void Imzalayan_turu_kurali(string type, string? kind, string? email, bool requireEmail, string? expected) =>
        Assert.Equal(expected, Signatures.SignerRule(type, kind, email, requireEmail));

    [Fact]
    public async Task Ic_uclar_teklif_mektubu_aday_ve_eposta_ister()
    {
        var (c, _) = Internal(Token);
        var r = await c.Otp(new("demo", "OfferLetter", Guid.NewGuid(), Guid.NewGuid(), "x", null, 1), default);
        Assert.Equal("invalid_signer", JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(r).Value).GetProperty("code").GetString());
        var (c2, _) = Internal(Token);
        r = await c2.Otp(new("demo", "OfferLetter", Guid.NewGuid(), Guid.NewGuid(), "x", null, 1, "Candidate", null), default);
        Assert.Equal("no_email", JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(r).Value).GetProperty("code").GetString());
        // Çalışan belgesine dış e-posta verilemez (kod başka adrese gönderilemesin).
        var (c3, _) = Internal(Token);
        r = await c3.Otp(new("demo", "HrDocument", Guid.NewGuid(), Guid.NewGuid(), "x", null, 1, null, "baska@example.com"), default);
        Assert.IsType<BadRequestObjectResult>(r);
        var (c4, _) = Internal(Token);
        r = await c4.Sign(new("demo", "OfferLetter", Guid.NewGuid(), Guid.NewGuid(), null, "123456", new string('a', 64), 1, null, null, null, null), default);
        Assert.IsType<BadRequestObjectResult>(r);
    }
}
