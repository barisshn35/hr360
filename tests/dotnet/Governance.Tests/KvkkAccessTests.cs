using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>KVKK m.11 veri paketi bölümleri, başvuru durum makinesi ve iz kodu türü denetimi.</summary>
public class PersonalDataExportSectionTests
{
    private static readonly string[] Required =
    {
        "calisan", "gorevlendirmeler", "profil", "ozelAlanlar", "izinTalepleri", "izinBakiyeleri", "puantaj", "kartBasmalar",
        "fazlaMesaiTalepleri", "masraflar", "masrafKalemleri", "seyahatTalepleri", "belgeTalepleri", "ucretGecmisi", "bordrolar",
        "performansHedefleri", "performansDegerlendirmeleri", "geriBildirimler", "egitimKayitlari", "sertifikalar", "iseAlimKaydi",
        "onboarding", "onboardingGorevleri", "istenAyrilis", "zimmetler", "saglikMuayeneleri", "asistanBaglami", "sohbetHesaplari",
        "bildirimler", "onaylar",
    };

    [Fact]
    public void Tum_kisisel_veri_kategorileri_pakette()
    {
        var keys = PersonalDataExport.Sections.Select(s => s.Key).Concat(PersonalDataExport.DerivedSections.Select(s => s.Key)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var k in Required) Assert.Contains(k, keys);
        Assert.Contains(PersonalDataExport.Feedback360Key, keys);
        Assert.Contains(PersonalDataExport.AccessLogKey, keys);
    }

    [Fact]
    public void Sorgular_salt_okuma_ve_kiraciya_bagli()
    {
        foreach (var s in PersonalDataExport.Sections)
        {
            var q = s.Query.Trim();
            Assert.StartsWith("SELECT", q);
            Assert.Contains("\"TenantSlug\" = $1", q);
            Assert.True(q.Contains("$2") || q.Contains("$3") || q.Contains("$4"), s.Key + ": kişiye göre süzülmeli");
            foreach (var w in new[] { "INSERT ", "UPDATE ", "DELETE ", "TRUNCATE", " LIMIT " })
                Assert.DoesNotContain(w, q.ToUpperInvariant());
            Assert.False(string.IsNullOrWhiteSpace(s.Tr));
            Assert.False(string.IsNullOrWhiteSpace(s.En));
            Assert.False(string.IsNullOrWhiteSpace(s.Category));
        }
    }

    [Fact]
    public void Ucret_ozel_nitelikli_ve_sinirli_bolumler_isaretli()
    {
        var by = PersonalDataExport.Sections.ToDictionary(s => s.Key);
        foreach (var k in new[] { "ucretGecmisi", "bordrolar", "avanslar", "yanHaklar", "sgkBilgileri" }) Assert.True(by[k].Compensation, k);
        Assert.False(by["izinTalepleri"].Compensation);
        var health = by["saglikMuayeneleri"];
        Assert.True(health.Special);
        Assert.Contains("KVKK m.6", health.NoteTr);
        Assert.Contains("art. 6", health.NoteEn);
        Assert.Single(PersonalDataExport.Sections, s => s.Special);
        Assert.Contains("90 days", by["bildirimler"].Query);
        // Şifreli ham alanlar sorguda açık metin olarak seçilmez.
        Assert.DoesNotContain("\"NotesEnc\",", health.Query);
        Assert.Contains("\"PassportCipher\" IS NOT NULL", by["seyahatTalepleri"].Query);
        Assert.Contains("'Hired'", by["iseAlimKaydi"].Query);
        Assert.DoesNotContain("PrivateNotes", by["birebirGorusmeler"].Query);
        Assert.DoesNotContain("FromEmployeeId", by["geriBildirimler"].Query);
    }

    [Fact]
    public void Gizli_sutunlar_ve_sifreli_baglam()
    {
        foreach (var c in new[] { "TenantSlug", "PassportCipher", "DocumentEnc", "NotesEnc", "LinkCodeHash", "SelfServiceTokenHash" })
            Assert.True(PersonalDataExport.IsHidden(c), c);
        Assert.False(PersonalDataExport.IsHidden("Email"));
        // Anahtar yoksa / çözülemezse metin yazılmaz; şifreli ham değer pakete girmez.
        var row = PersonalDataExport.Transform("asistanBaglami", new() { ["Role"] = "user", ["TextEnc"] = "bozuk" });
        Assert.False(row.ContainsKey("TextEnc"));
        Assert.True(row.ContainsKey("Text"));
        Assert.Null(row["Text"]);
    }

    [Fact]
    public void Eposta_normallestirme_ise_alimla_ayni()
    {
        Assert.Equal("ayse@demo.com", PersonalDataExport.NormalizeEmail("  Ayse+is@Demo.COM "));
        Assert.Equal("", PersonalDataExport.NormalizeEmail(null));
    }

    [Fact]
    public void Icerik_listesi_baslik_ve_sayilarla()
    {
        var bundle = new Dictionary<string, object?>
        {
            ["hazirlanma"] = DateTime.UtcNow,
            ["izinTalepleri"] = new List<object> { new { }, new { } },
            ["saglikMuayeneleri"] = new List<object>(),
            [PersonalDataExport.Feedback360Key] = new List<object>(),
        };
        var contents = PersonalDataExport.Contents(bundle);
        Assert.Equal(3, contents.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(contents);
        Assert.Contains("\"bolum\":\"izinTalepleri\"", json);
        Assert.Contains("\"kayit\":2", json);
        Assert.Contains("\"ozelNitelikli\":true", json);
        Assert.Contains("\"title\":\"Leave requests\"", json);
        bundle["_icerik"] = contents;
        var counts = DataRequestPackages.SectionCounts(bundle);
        Assert.False(counts.ContainsKey("_icerik"));
        Assert.Equal(2, counts["izinTalepleri"]);
    }

    private static (IReadOnlyDictionary<string, int>, string?) R(int a, int b, string? c = null) =>
        (new Dictionary<string, int> { ["a"] = a, ["b"] = b }, c);

    [Fact]
    public void Sonuc_360_kisiye_gosterildigi_gibi_toplulastirilir()
    {
        var comps = new[] { new PersonalDataExport.F360Competency("a", "İletişim"), new PersonalDataExport.F360Competency("b", "Takım") };
        var few = System.Text.Json.JsonSerializer.Serialize(PersonalDataExport.Aggregate360("t", null, comps, new[] { R(5, 5), R(4, 4) }, 5));
        Assert.Contains("\"hidden\":true", few);
        Assert.Contains("\"competencies\":[]", few);

        // b yetkinliğinde yalnızca 4 geçerli puan ("fikrim yok" = 0) → gizli; yorumlar metne göre sıralı.
        var resp = new[] { R(5, 0, "z yorum"), R(4, 3), R(3, 3, "a yorum"), R(4, 3), R(4, 3) };
        var json = System.Text.Json.JsonSerializer.Serialize(PersonalDataExport.Aggregate360("t", null, comps, resp, 5));
        Assert.Contains("\"hidden\":false,\"responses\":5", json);
        Assert.Contains("\"average\":4", json);
        Assert.Contains("\"Key\":\"b\",\"Label\":\"Takım\",\"ratings\":4,\"hidden\":true", json.Replace("\\u0131", "ı"));
        Assert.True(json.IndexOf("a yorum", StringComparison.Ordinal) < json.IndexOf("z yorum", StringComparison.Ordinal));
        Assert.DoesNotContain("Reviewer", json);
    }
}

public class DataRequestStateTests
{
    [Theory]
    [InlineData("Received", "InProgress", true)]
    [InlineData("Received", "Completed", true)]
    [InlineData("Received", "Rejected", true)]
    [InlineData("InProgress", "InProgress", true)]
    [InlineData("InProgress", "Completed", true)]
    [InlineData("InProgress", "Received", false)]
    [InlineData("Completed", "Completed", false)]
    [InlineData("Completed", "InProgress", false)]
    [InlineData("Rejected", "Completed", false)]
    [InlineData("Withdrawn", "Completed", false)]
    [InlineData("Withdrawn", "InProgress", false)]
    [InlineData("Received", "Withdrawn", false)] // İK geri çekemez
    public void Ik_gecisleri(string from, string to, bool ok) => Assert.Equal(ok, DataRequestStates.CanTransition(from, to));

    [Fact]
    public void Basvurucu_yalnizca_acik_basvuruyu_geri_ceker()
    {
        Assert.True(DataRequestStates.CanTransition("Received", "Withdrawn", byApplicant: true));
        Assert.True(DataRequestStates.CanTransition("InProgress", "Withdrawn", byApplicant: true));
        Assert.False(DataRequestStates.CanTransition("Completed", "Withdrawn", byApplicant: true));
        Assert.False(DataRequestStates.CanTransition("Withdrawn", "Withdrawn", byApplicant: true));
        Assert.False(DataRequestStates.CanTransition("Received", "Completed", byApplicant: true));
    }

    [Fact]
    public void Yanit_bildirimi_yalnizca_kapanista_bir_kez()
    {
        Assert.True(DataRequestStates.NotifiesApplicant("Received", "Completed"));
        Assert.True(DataRequestStates.NotifiesApplicant("InProgress", "Rejected"));
        Assert.False(DataRequestStates.NotifiesApplicant("InProgress", "InProgress"));
        Assert.False(DataRequestStates.NotifiesApplicant("Completed", "Completed"));
        Assert.False(DataRequestStates.NotifiesApplicant("Withdrawn", "Completed"));
    }
}

public class TracePolicyTests
{
    private static IReadOnlySet<string> Roles(params string[] r) => r.ToHashSet();

    [Theory]
    [InlineData("table:izin-talepleri", null, "csv", true)]
    [InlineData("table:bordro-2026-9", null, "csv", true)]
    [InlineData("table:puantaj-2026-09", null, "csv", true)]
    [InlineData("xlsx:all", null, "xlsx", true)]
    [InlineData("xlsx:employees", null, "xlsx", true)]
    [InlineData("document", "İş belgesi", "print", true)]
    [InlineData("payslip", "0e879b9e-d72b-489f-aa5b-8291e0bcbefb", "print", true)]
    [InlineData("certificate", "AB12-CD34", "print", true)]
    [InlineData("certificate", "AWS-123/45 B", "print", true)]
    [InlineData("certificate", null, "print", true)]
    [InlineData("anything", null, null, false)]
    [InlineData("table:uydurma", null, "csv", false)]
    [InlineData("table:izin-talepleri", "x", "csv", false)]
    [InlineData("payslip", "not-a-guid", "print", false)]
    [InlineData("payslip", null, "print", false)]
    [InlineData("certificate", "<script>", "print", false)]
    [InlineData("document", "x", "exe", false)]
    [InlineData("xlsx:../../etc", null, "xlsx", false)]
    public void Yalnizca_bilinen_turler(string kind, string? subject, string? format, bool valid) =>
        Assert.Equal(valid, TracePolicy.Evaluate(kind, subject, format).Kind is not null);

    [Fact]
    public void Konu_uzunlugu_ve_kontrol_karakteri()
    {
        Assert.Null(TracePolicy.Evaluate("document", new string('a', 101), "print").Kind);
        Assert.Null(TracePolicy.Evaluate("document", "a\nb", "print").Kind);
        Assert.Equal("subject", TracePolicy.Evaluate("document", "a\u0000", null).Error);
    }

    [Fact]
    public void Tur_basina_izin()
    {
        var employee = Roles("employee");
        Assert.True(TracePolicy.RolesAllow(TracePolicy.Evaluate("table:izin-talepleri", null, "csv"), employee));
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("table:bordro-2026-9", null, "csv"), employee));
        Assert.True(TracePolicy.RolesAllow(TracePolicy.Evaluate("table:bordro-2026-9", null, "csv"), Roles("ext-compensation-view")));
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("table:adaylar", null, "csv"), employee));
        Assert.True(TracePolicy.RolesAllow(TracePolicy.Evaluate("table:adaylar", null, "csv"), Roles("manager")));
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("table:kiracilar", null, "csv"), Roles("hr-admin")));
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("xlsx:all", null, "xlsx"), Roles("manager")));
        Assert.True(TracePolicy.RolesAllow(TracePolicy.Evaluate("xlsx:all", null, "xlsx"), Roles("hr-admin")));
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("document", "x", "print"), employee));
        // Pusula/sertifika: sahibi değilse rol gerekir (sahiplik veritabanında denetlenir).
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("payslip", Guid.NewGuid().ToString(), "print"), employee));
        Assert.False(TracePolicy.RolesAllow(TracePolicy.Evaluate("payslip", Guid.NewGuid().ToString(), "print"), Roles("manager")));
        Assert.True(TracePolicy.RolesAllow(TracePolicy.Evaluate("payslip", Guid.NewGuid().ToString(), "print"), Roles("hr-admin")));
        Assert.True(TracePolicy.RolesAllow(TracePolicy.Evaluate("certificate", "AB12", "print"), Roles("manager")));
    }
}

public class KvkkEnglishTextTests
{
    // Türkçeye özgü harfler. Hariç: yasal atıflardaki madde harfi ("m.5/2-ç"), kurum kısaltması (GİB) ve
    // kullanıcının bota yazdığı Türkçe komut ("geçmişimi sil").
    private static bool HasTurkish(string s) => System.Text.RegularExpressions.Regex.IsMatch(
        System.Text.RegularExpressions.Regex.Replace(s, @"\d-ç\b|GİB|geçmişimi sil", ""), "[ğışİĞŞıöüÖÜçÇ]");

    [Fact]
    public void Katalog_faaliyetlerinin_tum_alanlari_ingilizce()
    {
        foreach (var a in PrivacyCatalog.Activities.Select(PrivacyCatalogEn.Translate))
        {
            var texts = new[] { a.Module, a.Activity, a.Purpose, a.LegalBasis, a.Retention, a.Measures }
                .Concat(a.Subjects).Concat(a.DataCategories).Concat(a.Recipients);
            foreach (var t in texts) Assert.False(HasTurkish(t), $"{a.Id}: {t}");
        }
    }

    [Fact]
    public void Ozel_alan_faaliyeti_ve_duzenlenmis_metin()
    {
        Assert.Equal("Custom field: Kan grubu", PrivacyCatalogEn.T("Özel alan: Kan grubu"));
        Assert.Equal("Kan grubu (special category)", PrivacyCatalogEn.T("Kan grubu (özel nitelikli)"));
        Assert.Equal("Deleted automatically 24 months after leaving", PrivacyCatalogEn.T("İşten ayrılıştan 24 ay sonra otomatik silinir"));
        Assert.Equal("Former employees", PrivacyCatalogEn.T("Ayrılmış çalışanlar"));
        // Kiracının yazdığı metin değişmez.
        Assert.Equal("Kendi yazdığımız amaç", PrivacyCatalogEn.T("Kendi yazdığımız amaç"));
    }

    [Fact]
    public void Verbis_satirlari_ingilizce_basliklar_ve_sabitler()
    {
        var a = PrivacyCatalog.Activities.First(x => x.Id == "osh-health");
        var item = new VerbisInventory.Item(Guid.NewGuid(), a.Id, "Catalog", a.Module, a.Activity, a.Subjects, a.DataCategories, a.Purpose, a.LegalBasis,
            a.Special, a.Retention, a.RetentionCategory, a.Recipients, new[] { "zoom" }, a.Measures, true, DateTime.UtcNow, null);
        var transfers = new Dictionary<string, VerbisInventory.TransferInfo> { ["zoom"] = new("zoom", "Zoom", "ABD", "Dayanak kaydı yok", false) };
        var en = VerbisInventory.Rows(new[] { item }, transfers, en: true);
        Assert.All(en, r => Assert.Equal("Yes", r[8]));
        Assert.Contains("USA", en[0][7]);
        Assert.Contains("not in use", en[0][7]);
        Assert.All(en.SelectMany(r => r), v => Assert.False(HasTurkish(v), v));
        var csv = VerbisInventory.Csv(en, en: true);
        Assert.Contains("Data category;Personal data", csv);
        var tr = VerbisInventory.Rows(new[] { item }, transfers);
        Assert.Equal("Evet", tr[0][8]);
        Assert.Contains("Veri kategorisi", VerbisInventory.Csv(tr));
        Assert.Contains("lang=\"en\"", VerbisInventory.Html("Demo", en, DateTime.UtcNow, null, null, 1, en: true));
    }

    [Fact]
    public void Saklama_kategorisi_etiketleri_iki_dilde()
    {
        foreach (var k in Retention.Categories.Keys)
        {
            Assert.True(Retention.LabelsEn.ContainsKey(k), k);
            Assert.False(HasTurkish(Retention.LabelOf(k, en: true)), k);
            Assert.Equal(Retention.Categories[k].Label, Retention.LabelOf(k, en: false));
        }
        Assert.Equal("Bilinmeyen", Retention.LabelOf("Bilinmeyen", en: true));
    }

    [Fact]
    public void Imha_dogrulama_depo_haritasi_iki_dilde()
    {
        var tr = GovernanceService.Controllers.KvkkWave10Controller.StorageMap(false);
        var en = GovernanceService.Controllers.KvkkWave10Controller.StorageMap(true);
        Assert.Equal(tr.Length, en.Length);
        Assert.Equal(tr.Select(s => s.Location), en.Select(s => s.Location));
        foreach (var s in en)
            foreach (var t in new[] { s.Store, s.Personal, s.Destruction }) Assert.False(HasTurkish(t), t);
        Assert.Contains(tr, s => s.Personal.StartsWith("Evet"));
    }
}
