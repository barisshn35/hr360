using System.Text.Json;
using GovernanceService.Controllers;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>Dalga 5d: rapor ayrıştırıcısı (G3), küçük grup gizleme, özel alanlar (Y24), OTP imza (Y28), REST hook (G29).</summary>
public class NlReportParserTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly Guid Eng = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Sales = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly List<(Guid, string)> Depts = new() { (Eng, "Mühendislik"), (Sales, "Satış") };

    private static NlReport.Query P(string q, bool hr = true, bool en = false) => NlReport.Parse(q, Today, Depts, hr, en);

    [Theory]
    [InlineData("Departmanlara göre maaş dağılımı", "salary", "department")]
    [InlineData("Salary distribution by department", "salary", "department")]
    [InlineData("şirket geneli maaş dağılımı", "salary", "none")]
    [InlineData("maaş bantlarına göre çalışan sayısı", "salary", "band")]
    [InlineData("Salary bands", "salary", "band")]
    [InlineData("Bu yıl ilanlara göre işe alım hunisi", "funnel", "posting")]
    [InlineData("Recruitment funnel by posting this year", "funnel", "posting")]
    [InlineData("aylık başvuru hunisi", "funnel", "month")]
    [InlineData("Applications per stage by department", "funnel", "department")]
    [InlineData("Son 6 ayda departmanlara göre izin günleri", "leave", "department")]
    [InlineData("Monthly hires this year", "hires", "month")]
    [InlineData("Bu yıl ücretsiz izin günleri", "leave", "none")]
    public void Olcut_ve_kirilim(string q, string metric, string group)
    {
        var r = P(q);
        Assert.Equal(metric, r.Metric);
        Assert.Equal(group, r.GroupBy);
    }

    [Fact]
    public void Ise_alim_hunisi_ise_alim_sayisi_degildir()
    {
        Assert.Equal("funnel", P("işe alım hunisi").Metric);
        Assert.Equal("hires", P("işe alım sayısı").Metric);
    }

    [Theory]
    [InlineData("Bu yıl izin günleri geçen yıla göre", "leave")]
    [InlineData("Headcount by department compared to last year", "headcount")]
    [InlineData("Leavers this year year over year", "exits")]
    [InlineData("Bu yıl ayrılan çalışan sayısı geçen yılla karşılaştır", "exits")]
    public void Gecen_yilla_karsilastirma(string q, string metric)
    {
        var r = P(q);
        Assert.True(r.Compare);
        Assert.Equal(metric, r.Metric);
    }

    [Fact]
    public void Karsilastirma_ifadesi_donemi_gecen_yil_yapmaz()
    {
        var r = P("Bu yıl izin günleri geçen yıla göre");
        Assert.Equal(new DateOnly(2026, 1, 1), r.From);
        Assert.Equal(Today, r.To);
        var e = P("Leave days this year compared to last year", en: true);
        Assert.True(e.Compare);
        Assert.Equal(new DateOnly(2026, 1, 1), e.From);
    }

    [Fact]
    public void Karsilastirma_desteklenmeyen_olcutte_kapali() => Assert.False(P("ortalama performans puanı geçen yıla göre").Compare);

    [Fact]
    public void Departman_suzgeci_kirilimi_bozmaz()
    {
        var r = P("Mühendislik departmanında bu yıl izin günleri");
        Assert.Equal(Eng, r.DepartmentId);
        Assert.Equal("Mühendislik", r.Department);
        Assert.Equal("none", r.GroupBy);
        Assert.Equal("leave", r.Metric);
        var m = P("Satış biriminde aylık işe alım");
        Assert.Equal(Sales, m.DepartmentId);
        Assert.Equal("month", m.GroupBy);
        Assert.Null(P("departmanlara göre izin").DepartmentId);
    }

    [Fact]
    public void Tarih_araligi_gun_ay_yil_ve_iso()
    {
        var r = P("01.02.2026 ile 31.03.2026 arasında izin günleri");
        Assert.Equal(new DateOnly(2026, 2, 1), r.From);
        Assert.Equal(new DateOnly(2026, 3, 31), r.To);
        Assert.Contains("01.02.2026", r.PeriodLabel);
        var iso = P("leave days between 2026-03-31 and 2026-01-15", en: true);
        Assert.Equal(new DateOnly(2026, 1, 15), iso.From);
        Assert.Equal(new DateOnly(2026, 3, 31), iso.To);
        Assert.StartsWith("between", iso.PeriodLabel);
    }

    [Fact]
    public void Ay_araligi()
    {
        var r = P("ocak-mart 2026 izin günleri");
        Assert.Equal(new DateOnly(2026, 1, 1), r.From);
        Assert.Equal(new DateOnly(2026, 3, 31), r.To);
        var e = P("hires from january to june", en: true);
        Assert.Equal(new DateOnly(2026, 1, 1), e.From);
        Assert.Equal(new DateOnly(2026, 6, 30), e.To);
    }

    [Fact]
    public void Maas_araliklari_aralik_ayi_degildir()
    {
        var r = P("maaş aralıkları");
        Assert.True(r.Bands);
        Assert.False(r.From.Day == 1 && r.From.Month == 12 && r.To.Month == 12);
        Assert.True(P("maaş aralığı").Bands);
    }

    [Fact]
    public void Kisi_bazi_yetkisizse_departmana_duser()
    {
        Assert.Equal("person", P("en çok fazla mesai yapanlar", hr: true).GroupBy);
        Assert.Equal("department", P("en çok fazla mesai yapanlar", hr: false).GroupBy);
    }

    [Fact]
    public void Mevcut_davranis_korunur()
    {
        var r = P("Geçen ay en çok fazla mesai yapan çalışanlar");
        Assert.Equal("overtime", r.Metric);
        Assert.Equal(new DateOnly(2026, 9, 1), r.From);
        Assert.Equal(new DateOnly(2026, 9, 30), r.To);
        Assert.Null(P("hava nasıl").Metric);
    }
}

public class ReportPrivacyTests
{
    [Fact]
    public void Kucuk_gruplar_gizlenir_sifir_gizlenmez()
    {
        var rows = new List<object?[]> { new object?[] { "A", 10m, 3L }, new object?[] { "B", 50m, 5L }, new object?[] { "C", null, 0L } };
        var n = NlReport.Suppress(rows, 2, new[] { 1 });
        Assert.Equal(1, n);
        Assert.Null(rows[0][1]);
        Assert.Equal(50m, rows[1][1]);
    }

    [Fact]
    public void Gecen_yil_birlestirme_aylik_kaydirir_ve_degisim_hesaplar()
    {
        var cur = new List<object?[]> { new object?[] { "2026-01", 12m, 6L }, new object?[] { "2026-02", 10m, 4L } };
        var prev = new List<object?[]> { new object?[] { "2025-01", 8m, 7L }, new object?[] { "2025-03", 5m, 9L } };
        var m = NlReport.MergeYoY(cur, prev, monthly: true);
        Assert.Equal(3, m.Count);
        Assert.Equal(new object?[] { "2026-01", 12m, 8m, 50.0m, 6L }, m[0]);
        Assert.Null(m[1][3]); // geçen yıl yok → değişim yok
        Assert.Equal("2026-03", m[2][0]);
        Assert.Equal(0m, m[2][1]);
    }

    [Fact]
    public void Ceyrekler_dogrusal()
    {
        var s = new List<decimal> { 10, 20, 30, 40, 50 };
        Assert.Equal(20m, NlReport.Quantile(s, 0.25));
        Assert.Equal(30m, NlReport.Quantile(s, 0.5));
        Assert.Equal(25m, NlReport.Quantile(new List<decimal> { 10, 20, 30, 40 }, 0.5));
    }

    [Fact]
    public void Ucret_dagilimi_bes_kisiden_az_departmani_gizler()
    {
        var data = new List<(string, decimal, string)>();
        for (var i = 0; i < 6; i++) data.Add(("Mühendislik", 50_000 + i * 1_000, "TRY"));
        data.Add(("Satış", 40_000, "TRY"));
        data.Add(("Satış", 42_000, "TRY"));
        var (cols, rows, suppressed, note) = NlReport.SalaryStats(data, bands: false, byDepartment: true, en: false);
        Assert.Equal(5, cols.Length);
        Assert.Equal(1, suppressed);
        Assert.NotNull(note);
        var eng = rows.Single(r => (string)r[0]! == "Mühendislik");
        Assert.Equal(6L, eng[1]);
        Assert.Equal(52_500m, eng[3]); // medyan, 100'e yuvarlı
        var sales = rows.Single(r => (string)r[0]! == "Satış");
        Assert.All(sales.Skip(1), Assert.Null); // kişi sayısı bile gösterilmez
    }

    [Fact]
    public void Ucret_bantlari_toplam_kucukse_tamamen_gizli()
    {
        var few = new List<(string, decimal, string)> { ("x", 30_000, "TRY"), ("x", 70_000, "TRY") };
        var (_, rows, suppressed, _) = NlReport.SalaryStats(few, bands: true, byDepartment: false, en: true);
        Assert.Equal(NlReport.SalaryBands.Length, suppressed);
        Assert.All(rows, r => Assert.Null(r[1]));
        var many = Enumerable.Range(0, 8).Select(i => ("x", 30_000m + i * 10_000m, "TRY")).ToList();
        var (_, rows2, s2, _) = NlReport.SalaryStats(many, bands: true, byDepartment: false, en: false);
        Assert.Equal(0, s2);
        Assert.Equal(8L, rows2.Sum(r => (long)r[1]!));
    }
}

public class CustomFieldTests
{
    private static CustomFields.Definition D(string key = "shoe_size", string type = "number", bool special = false, string basis = "m5-2-c",
        string visibility = "hr", int months = 12, List<string>? options = null) =>
        new(key, "Etiket", type, options, false, visibility, true, special, basis, "Personel kıyafeti tedariki", months);

    [Fact]
    public void Gecerli_tanim() => Assert.Null(CustomFields.Validate(D()));

    [Theory]
    [InlineData("Shoe", "anahtar")]
    [InlineData("1abc", "anahtar")]
    public void Gecersiz_anahtar(string key, string _) => Assert.NotNull(CustomFields.Validate(D(key: key)));

    [Fact]
    public void Ozel_nitelikli_m6_ister_ve_herkese_acilamaz()
    {
        Assert.NotNull(CustomFields.Validate(D(special: true, basis: "m5-2-c")));
        Assert.NotNull(CustomFields.Validate(D(special: false, basis: "m6-3-e")));
        Assert.NotNull(CustomFields.Validate(D(special: true, basis: "m6-3-e", visibility: "everyone")));
        Assert.NotNull(CustomFields.Validate(D(special: true, basis: "m6-3-e", visibility: "manager")));
        Assert.Null(CustomFields.Validate(D(special: true, basis: "m6-3-e", visibility: "hr")));
    }

    [Fact]
    public void Saklama_ve_secenek_dogrulamasi()
    {
        Assert.NotNull(CustomFields.Validate(D(months: 0)));
        Assert.NotNull(CustomFields.Validate(D(type: "select", options: new() { "tek" })));
        Assert.Null(CustomFields.Validate(D(type: "select", options: new() { "S", "M", "L" })));
        Assert.NotNull(CustomFields.Validate(D(basis: "yok")));
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Deger_normallestirme()
    {
        Assert.Equal("42.5", CustomFields.Normalize("number", null, J("\"42,5\"")).Value);
        Assert.False(CustomFields.Normalize("number", null, J("\"abc\"")).Ok);
        Assert.Equal("2026-01-05", CustomFields.Normalize("date", null, J("\"2026-01-05\"")).Value);
        Assert.False(CustomFields.Normalize("date", null, J("\"05.01.2026\"")).Ok);
        Assert.Equal("true", CustomFields.Normalize("boolean", null, J("true")).Value);
        Assert.Equal("M", CustomFields.Normalize("select", new[] { "S", "M" }, J("\"M\"")).Value);
        Assert.False(CustomFields.Normalize("select", new[] { "S", "M" }, J("\"XL\"")).Ok);
        var empty = CustomFields.Normalize("text", null, J("\"  \""));
        Assert.True(empty.Ok);
        Assert.Null(empty.Value);
    }

    [Theory]
    [InlineData("everyone", "other", true)]
    [InlineData("manager", "other", false)]
    [InlineData("manager", "manager", true)]
    [InlineData("hr", "manager", false)]
    [InlineData("hr", "hr", true)]
    [InlineData("self", "hr", false)]
    [InlineData("self", "self", true)]
    public void Gorunurluk(string level, string viewer, bool expected) => Assert.Equal(expected, CustomFields.CanSee(level, viewer));

    [Fact]
    public void Envanter_faaliyeti()
    {
        var f = new CustomFields.FieldRow(Guid.NewGuid(), "blood_type", "Kan grubu", "select", new() { "A", "B" }, false, "hr", true, true,
            "m6-3-e", "Acil durumda sağlık müdahalesi", 6, Guid.NewGuid(), true, 0, "İK", DateTime.UtcNow);
        var a = CustomFields.Activity(f);
        Assert.Equal("custom-field-blood_type", a.Id);
        Assert.True(a.Special);
        Assert.Equal("CustomFieldValues", a.RetentionCategory);
        Assert.Contains("6 ay", a.Retention);
        Assert.Contains("m.6/3-e", a.LegalBasis);
    }
}

// İmza kodu özeti TENANT_SECRET_KEY'den türetilir; bu anahtarı değiştiren testlerle paralel koşmamalı.
[Collection("TenantSecretKey")]
public class SignatureAndHookTests
{
    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("::ffff:198.51.100.9", "198.51.100.0/24")]
    [InlineData("2001:db8:abcd:12:1:2:3:4", "2001:db8:abcd::/48")]
    [InlineData("10.1.2.3, 172.16.0.1", "10.1.2.0/24")]
    [InlineData("bozuk", null)]
    public void Ip_kisaltma(string ip, string? expected) => Assert.Equal(expected, Signatures.TruncateIp(ip));

    [Fact]
    public void Kod_ozeti_tuzlu_ve_sabit_zamanli_esles()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var code = Signatures.NewCode();
        Assert.Matches("^[0-9]{6}$", code);
        var h = Signatures.Hash(a, code);
        Assert.DoesNotContain(code, h);
        Assert.NotEqual(h, Signatures.Hash(b, code));
        Assert.True(Signatures.Matches(a, code, h));
        Assert.False(Signatures.Matches(a, code == "000000" ? "000001" : "000000", h));
    }

    [Fact]
    public void Kod_durumu()
    {
        var now = DateTime.UtcNow;
        Assert.Equal("ok", Signatures.State(now.AddMinutes(5), 4, null, now));
        Assert.Equal("locked", Signatures.State(now.AddMinutes(5), 5, null, now));
        Assert.Equal("expired", Signatures.State(now.AddSeconds(-1), 0, null, now));
        Assert.Equal("consumed", Signatures.State(now.AddMinutes(5), 0, now, now));
    }

    [Fact]
    public void Kanit_ozeti_alanlara_bagli()
    {
        var id = Guid.NewGuid(); var who = Guid.NewGuid(); var at = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        var c1 = Signatures.Canonical("DocumentRequest", id, 1, "ab", who, at, "OTP-InApp", "1.2.3.0/24", Signatures.DisclaimerTr);
        Assert.Equal(c1, Signatures.Canonical("DocumentRequest", id, 1, "ab", who, at, "OTP-InApp", "1.2.3.0/24", Signatures.DisclaimerTr));
        Assert.NotEqual(c1, Signatures.Canonical("DocumentRequest", id, 1, "ac", who, at, "OTP-InApp", "1.2.3.0/24", Signatures.DisclaimerTr));
        Assert.NotEqual(c1, Signatures.Canonical("HrDocument", id, 1, "ab", who, at, "OTP-InApp", "1.2.3.0/24", Signatures.DisclaimerTr));
        Assert.Contains("5070", c1);
    }

    [Theory]
    [InlineData("https://hooks.zapier.com/hooks/catch/1/abc", "zapier")]
    [InlineData("https://zapier.com/x", "zapier")]
    [InlineData("https://HOOKS.ZAPIER.COM./x", "zapier")]
    [InlineData("https://notzapier.com/x", null)]
    [InlineData("https://n8n.sirket.local/webhook/1", null)]
    [InlineData("http://chatmock:8000/hook", null)]
    public void Zapier_hedefi_yurt_disi_aktarimdir(string url, string? expected) => Assert.Equal(expected, TransferGuard.HookProvider(url));

    [Fact]
    public void Zapier_saglayici_katalogda() => Assert.Contains(PrivacyCatalog.Providers, p => p.Key == "zapier" && p.Country == "ABD");

    [Fact]
    public void Ornek_veri_sentetik()
    {
        var json = JsonSerializer.Serialize(HookSamples.Envelope("demo", "employee.hired", new[] { "EmployeeId", "FirstName", "Email" }));
        Assert.Contains("example.com", json);
        Assert.Contains("\"sample\":true", json);
    }

    [Theory]
    [InlineData("Daily", "2026-10-03T03:00:00Z", "2026-10-03T04:00:00Z")]  // 06:00 İstanbul → bugün 07:00
    [InlineData("Daily", "2026-10-03T05:00:00Z", "2026-10-04T04:00:00Z")]
    [InlineData("Weekly", "2026-10-03T05:00:00Z", "2026-10-05T04:00:00Z")] // cumartesi → pazartesi
    [InlineData("Monthly", "2026-10-03T05:00:00Z", "2026-11-01T04:00:00Z")]
    public void Zamanlama(string schedule, string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), ReportSchedule.Next(schedule, DateTime.Parse(now).ToUniversalTime()));

    [Fact]
    public void Zamanlama_yok() => Assert.Null(ReportSchedule.Next("None", DateTime.UtcNow));

    [Theory]
    [InlineData("Daily", "08:30", null, "2026-10-03T05:00:00Z", "2026-10-03T05:30:00Z")]   // 08:00 İstanbul → bugün 08:30
    [InlineData("Daily", "08:30", null, "2026-10-03T05:30:00Z", "2026-10-04T05:30:00Z")]   // tam saatinde → yarın
    [InlineData("Daily", "23:45", null, "2026-10-03T21:00:00Z", "2026-10-04T20:45:00Z")]   // gece yarısı sonrası (İstanbul 4 Ekim 00:00)
    [InlineData("Weekly", "09:00", 5, "2026-10-03T05:00:00Z", "2026-10-09T06:00:00Z")]    // cumartesi → cuma
    [InlineData("Weekly", "09:00", 6, "2026-10-03T05:00:00Z", "2026-10-03T06:00:00Z")]    // cumartesi 08:00 → bugün 09:00
    [InlineData("Weekly", "09:00", 7, "2026-10-03T05:00:00Z", "2026-10-04T06:00:00Z")]    // pazar
    [InlineData("Monthly", "10:00", 15, "2026-10-03T05:00:00Z", "2026-10-15T07:00:00Z")]
    [InlineData("Monthly", "10:00", 15, "2026-12-20T05:00:00Z", "2027-01-15T07:00:00Z")]  // yıl devri
    public void Zamanlama_saat_ve_gun(string schedule, string time, int? day, string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), ReportSchedule.Next(schedule, DateTime.Parse(now).ToUniversalTime(), time, day));

    [Theory]
    [InlineData("08:30", true)]
    [InlineData("8:05", true)]
    [InlineData("24:00", false)]
    [InlineData("0830", false)]
    [InlineData("", false)]
    public void Saat_bicimi(string s, bool ok) => Assert.Equal(ok, ReportSchedule.ParseTime(s) is not null);

    [Theory]
    [InlineData("https://acme.app.n8n.cloud/webhook/abc", "n8n")]
    [InlineData("https://n8n.cloud/webhook/abc", "n8n")]
    [InlineData("https://n8n.sirketim.com.tr/webhook/abc", null)]
    public void N8n_bulut_yurt_disi_kendi_sunucusu_degil(string url, string? expected) => Assert.Equal(expected, TransferGuard.HookProvider(url));

    [Theory]
    [InlineData("http://n8n:5678/webhook/x", true)]
    [InlineData("http://localhost:5678/webhook/x", true)]
    [InlineData("http://10.1.2.3/webhook", true)]
    [InlineData("http://172.20.0.5/webhook", true)]
    [InlineData("http://192.168.1.10/webhook", true)]
    [InlineData("http://n8n.ofis.local/webhook", true)]
    [InlineData("http://[fd00::1]/webhook", true)]
    [InlineData("https://n8n.sirketim.com.tr/webhook", false)]
    [InlineData("http://172.32.0.1/webhook", false)]
    [InlineData("https://hooks.zapier.com/x", false)]
    public void Ic_ag_hedefi(string url, bool expected) => Assert.Equal(expected, TransferGuard.IsInternalHost(url));

    [Theory]
    [InlineData("https://hooks.zapier.com/x", "zapier")]
    [InlineData("https://acme.app.n8n.cloud/webhook/abc", "n8n-cloud")]
    [InlineData("http://n8n:5678/webhook/x", "internal")]
    [InlineData("https://n8n.sirketim.com.tr/webhook", "self-hosted")]
    public void Hook_hedef_sinifi(string url, string expected) => Assert.Equal(expected, PublicApiController.Deployment(url));

    [Fact]
    public void N8n_bulut_katalogda() => Assert.Contains(PrivacyCatalog.Providers, p => p.Key == "n8n");

    [Fact]
    public void Kucuk_grup_gizleme_varsayilan_acik() => Assert.True(new NlReport.Options().Suppress);
}
