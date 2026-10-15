using System.Security.Cryptography;
using System.Text;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>Dalga 5c: iş günü takvimi, hedef kitle, etik takip kodu, İSG ve disiplin kuralları.</summary>
// TENANT_SECRET_KEY süreç genelinde değiştirildiği için bu sınıflar paralel koşmaz (rastgele AES hatası).
[Collection("TenantSecretKey")]
public class WorkplaceComplianceTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private static UserInfo U(params string[] roles) => new("u1", "Test", null, roles.ToHashSet());

    [Theory]
    [InlineData("2026-10-02", 3, "2026-10-07")] // Cuma kazası → Çarşamba
    [InlineData("2026-10-05", 3, "2026-10-08")] // Pazartesi → Perşembe
    [InlineData("2026-10-03", 3, "2026-10-07")] // Cumartesi → Pzt, Sal, Çar
    [InlineData("2026-10-08", 2, "2026-10-12")] // Perşembe + 2 → Pazartesi
    public void Is_gunu_ekleme_hafta_sonunu_atlar(string start, int days, string expected) =>
        Assert.Equal(D(expected), BusinessCalendar.AddBusinessDays(D(start), days));

    [Fact]
    public void Is_gunu_ekleme_resmi_tatili_atlar()
    {
        var holidays = new HashSet<DateOnly> { D("2026-10-29") }; // Cumhuriyet Bayramı (Perşembe)
        Assert.Equal(D("2026-11-02"), Osh.SgkDeadline(D("2026-10-27"), holidays));
        Assert.Equal(D("2026-10-30"), Osh.SgkDeadline(D("2026-10-27")));
    }

    [Fact]
    public void Savunma_suresi_en_az_iki_is_gunu()
    {
        Assert.Equal(D("2026-10-06"), Disciplinary.MinDeadline(D("2026-10-03"))); // Cumartesi
        Assert.Equal(D("2026-10-13"), Disciplinary.MinDeadline(D("2026-10-09"))); // Cuma
    }

    [Fact]
    public void Hedef_kitle_kurallari()
    {
        var eng = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.True(Audience.CanSee("All", Array.Empty<Guid>(), false, false, null));
        Assert.False(Audience.CanSee("Managers", Array.Empty<Guid>(), false, false, eng));
        Assert.True(Audience.CanSee("Managers", Array.Empty<Guid>(), false, true, eng));
        Assert.False(Audience.CanSee("Hr", Array.Empty<Guid>(), false, true, eng));
        Assert.True(Audience.CanSee("Hr", Array.Empty<Guid>(), true, true, null));
        Assert.True(Audience.CanSee("Departments", new[] { eng }, false, false, eng));
        Assert.False(Audience.CanSee("Departments", new[] { other }, false, true, eng));
        Assert.False(Audience.CanSee("Departments", new[] { other }, false, false, null));
        Assert.True(Audience.CanSee("Departments", new[] { other }, true, false, null)); // İK yönetir
        Assert.False(Audience.CanSee("Bilinmeyen", Array.Empty<Guid>(), false, true, eng));
    }

    [Fact]
    public void Hedef_kitle_uyeleri()
    {
        var eng = Guid.NewGuid();
        Person P(Guid? dept) => new(Guid.NewGuid(), "X", null, null, dept, null, D("2020-01-01"), "Active", "u", null);
        var a = P(eng);
        var b = P(null);
        var people = new[] { a, b };
        Assert.Equal(2, Audience.Members("All", Array.Empty<Guid>(), people, new HashSet<Guid>())!.Count);
        Assert.Single(Audience.Members("Departments", new[] { eng }, people, new HashSet<Guid>())!);
        Assert.Single(Audience.Members("Managers", Array.Empty<Guid>(), people, new HashSet<Guid> { b.Id })!);
        Assert.Null(Audience.Members("Hr", Array.Empty<Guid>(), people, new HashSet<Guid>()));
    }

    [Fact]
    public void Etik_takip_kodu_rastgele_ve_ozetle_saklanir()
    {
        var code = EthicsCode.New();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.True(EthicsCode.LooksValid(code));
        Assert.NotEqual(code, EthicsCode.New());
        // Tire/küçük harf/boşluk fark etmez; özet normalleştirilmiş kodun SHA-256'sı.
        var loose = " " + code.Replace("-", "").ToLowerInvariant() + " ";
        Assert.Equal(EthicsCode.Hash(code), EthicsCode.Hash(loose));
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("-", "")))).ToLowerInvariant();
        Assert.Equal(expected, EthicsCode.Hash(code));
        Assert.DoesNotContain(code.Replace("-", ""), EthicsCode.Hash(code), StringComparison.OrdinalIgnoreCase);
        Assert.False(EthicsCode.LooksValid("ABCD-EFGH"));
        Assert.False(EthicsCode.LooksValid("ABCD-EFGH-IJKL-0000")); // I ve 0 alfabede yok
    }

    [Fact]
    public void Pencere_sinirlayici()
    {
        var l = new WindowLimiter(2, TimeSpan.FromMinutes(1));
        var t = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(l.Allow("k", t));
        Assert.True(l.Allow("k", t));
        Assert.False(l.Allow("k", t));
        Assert.True(l.Allow("baska", t));
        Assert.True(l.Allow("k", t.AddMinutes(2)));
    }

    [Fact]
    public void Isg_rolleri_ve_yetkiler()
    {
        Assert.True(Osh.IsPhysician(U("osh-physician")));
        Assert.True(Osh.IsPhysician(U("ext-osh-physician")));
        Assert.False(Osh.IsPhysician(U("hr-admin", "tenant-admin")));
        Assert.False(Osh.IsPhysician(U("osh-specialist")));
        Assert.True(Osh.CanManage(U("hr-admin")));
        Assert.True(Osh.CanManage(U("osh-specialist")));
        Assert.False(Osh.CanManage(U("manager")));
        Assert.False(Osh.CanManage(U("employee")));
    }

    [Theory]
    [InlineData("VeryHazardous", "2027-03-01")]
    [InlineData("Hazardous", "2029-03-01")]
    [InlineData("LessHazardous", "2031-03-01")]
    [InlineData(null, "2029-03-01")]
    public void Periyodik_muayene_araligi(string? hazard, string expected) =>
        Assert.Equal(D(expected), Osh.NextExam(D("2026-03-01"), hazard));

    [Fact]
    public void Muayene_vade_durumu()
    {
        var today = D("2026-10-03");
        Assert.Equal("Overdue", Osh.DueState(D("2026-10-02"), today));
        Assert.Equal("DueSoon", Osh.DueState(D("2026-10-20"), today));
        Assert.Equal("Ok", Osh.DueState(D("2027-01-01"), today));
        Assert.Equal("None", Osh.DueState(null, today));
        Assert.Equal("DueSoon", Osh.DueState(D("2026-11-25"), today, 60));
    }

    [Fact]
    public void Saglik_notu_sifreli_saklanir()
    {
        var prev = Environment.GetEnvironmentVariable("TENANT_SECRET_KEY");
        Environment.SetEnvironmentVariable("TENANT_SECRET_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        try
        {
            const string note = "Bel fıtığı; ağır kaldırma kısıtı";
            var enc = SecretBox.Protect(note)!;
            Assert.DoesNotContain("fıtığı", enc);
            Assert.NotEqual(enc, SecretBox.Protect(note)); // rastgele nonce
            Assert.Equal(note, SecretBox.Unprotect(enc));
        }
        finally { Environment.SetEnvironmentVariable("TENANT_SECRET_KEY", prev); }
    }

    [Fact]
    public void Adli_sicil_uyarisi()
    {
        Assert.NotEmpty(Disciplinary.CriminalRecordWarnings("Çalışanın SABIKA kaydı var"));
        Assert.NotEmpty(Disciplinary.CriminalRecordWarnings(null, "Hapis cezası almış"));
        Assert.NotEmpty(Disciplinary.CriminalRecordWarnings("adli sicil belgesi istendi"));
        Assert.Empty(Disciplinary.CriminalRecordWarnings("Üç gün üst üste geç geldi"));
        Assert.Empty(Disciplinary.CriminalRecordWarnings(null, "  "));
    }

    [Fact]
    public void Savunma_istem_yazisi()
    {
        var text = Disciplinary.DefenceNotice("Ayşe Yılmaz", "Demo Şirket", D("2026-09-28"), "Devamsızlık / geç gelme", "Üç gün geç geldi.", D("2026-10-06"));
        Assert.Contains("Sayın Ayşe Yılmaz", text);
        Assert.Contains("28.09.2026", text);
        Assert.Contains("06.10.2026", text);
        Assert.Contains("Salı", text);
        Assert.Contains("Demo Şirket", text);
        Assert.Contains("Üç gün geç geldi.", text);
    }
}
