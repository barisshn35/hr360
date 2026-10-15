using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>Dalga 10: imha planı kapsamı, nesne deposu kuyruğu, başvuru süre takibi, veri paketi, yeniden onay kampanyası, VERBİS dışa aktarımı.</summary>
public class RetentionPlanTests
{
    [Fact]
    public void Calisan_adimlari_kiraciya_bagli_ve_calisan_kaydi_en_sonda()
    {
        Assert.All(RetentionPlans.EmployeeSteps, s =>
        {
            Assert.Contains("\"TenantSlug\" = $1", s.Exec);
            Assert.Contains("WHERE", s.Exec);
            Assert.Contains("WHERE", s.Count);
            Assert.StartsWith(s.Kind == "Delete" ? "DELETE FROM " + s.Table : "UPDATE " + s.Table, s.Exec);
        });
        // Kullanıcı kimliğiyle ($3) bağlanan tablolar KeycloakUserId silinmeden önce işlenmeli.
        Assert.Equal("employee_employees", RetentionPlans.EmployeeSteps[^1].Table);
    }

    [Theory]
    [InlineData("engagement_profiles")]
    [InlineData("leave_requests")]
    [InlineData("timeshift_time_entries")]
    [InlineData("timeshift_clock_credentials")]
    [InlineData("expense_travel_requests")]
    [InlineData("notification_messages")]
    [InlineData("governance_chat_context")]
    [InlineData("governance_consents")]
    [InlineData("engagement_internal_applications")]
    [InlineData("engagement_offboarding_cases")]
    [InlineData("performance_reviews")]
    public void Tum_servislerin_kisisel_alanlari_kapsamda(string table) =>
        Assert.Contains(RetentionPlans.EmployeeSteps, s => s.Table == table);

    [Theory]
    [InlineData("compensation_payslips", true)]
    [InlineData("learning_enrollments", true)]
    [InlineData("expense_claims", true)]
    [InlineData("engagement_profiles", false)]
    [InlineData("bilinmeyen_tablo", false)]
    public void Istisna_gerekcesi(string table, bool exempt) =>
        Assert.Equal(exempt, RetentionPlans.ExemptReason(table) is not null);

    [Fact]
    public void Kapsanan_tablo_istisna_listesinde_olmamali()
    {
        foreach (var s in RetentionPlans.EmployeeSteps)
            Assert.Null(RetentionPlans.ExemptReason(s.Table));
    }

    [Fact]
    public void Aday_anonimlestirme_ozgecmis_dosyasini_ve_metnini_temizler()
    {
        var cand = RetentionPlans.CandidateAnonymizeSteps.Single(s => s.Table == "recruitment_candidates");
        Assert.Contains("\"ResumeStorageKey\" = NULL", cand.Exec);
        Assert.Contains("\"ResumeText\" = NULL", cand.Exec);
        Assert.Contains("\"AnonymizedAt\" = now()", cand.Exec);
        // Dosya anahtarı kayıt değişmeden önce okunur (silme kuyruğu).
        Assert.Contains(RetentionPlans.CandidateStorage, r => r.Column == "ResumeStorageKey");
        Assert.Equal("recruitment_candidates", RetentionPlans.CandidateAnonymizeSteps[^1].Table);
    }

    [Theory]
    [InlineData("AuditLog")]
    [InlineData("Notifications")]
    [InlineData("Payslips")]
    [InlineData("Announcements")]
    [InlineData("CustomFieldValues")]
    public void Tek_tablolu_kategorilerin_onizlemesi_ayni_kosulla(string category)
    {
        var steps = RetentionPlans.SimpleSteps(category);
        Assert.NotEmpty(steps);
        Assert.All(steps, s => { Assert.Contains("$1", s.Count); Assert.Contains("$2", s.Count); Assert.StartsWith("SELECT count(*)", s.Count.Trim()); });
        Assert.Equal(category == "AuditLog", steps.Any(s => s.UseRetentionConnection));
    }

    [Fact]
    public void Her_kategorinin_plani_var()
    {
        foreach (var c in Retention.Categories.Keys.Where(k => k is not ("TerminatedEmployees" or "RejectedCandidates")))
            Assert.NotEmpty(RetentionPlans.SimpleSteps(c));
    }

    [Fact]
    public void Nesne_deposu_ozeti_ve_durum_eslemesi()
    {
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cv/a.pdf"))).ToLowerInvariant(), StorageDeletions.Hash("cv/a.pdf"));
        Assert.Equal("Deleted", StorageDeletions.MapStatus("Deleted"));
        Assert.Equal("Absent", StorageDeletions.MapStatus("Absent"));
        Assert.Equal("NotStored", StorageDeletions.MapStatus("NoBucket"));
        Assert.Equal("NotStored", StorageDeletions.MapStatus("NotAllowed"));
        Assert.Equal("Failed", StorageDeletions.MapStatus(null));
        Assert.Equal("Failed", StorageDeletions.MapStatus("tuhaf"));
    }
}

public class DataRequestReminderTests
{
    private static readonly DateTime C = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static DateTime Due => C.AddDays(30);

    [Theory]
    [InlineData(5, false, false, false, null)]
    [InlineData(20, false, false, false, "d20")]
    [InlineData(21, true, false, false, null)]
    [InlineData(27, true, false, false, "d27")]
    [InlineData(27, false, false, false, "d27")]   // 20. gün kaçırıldıysa yalnızca güncel aşama
    [InlineData(29, true, true, false, null)]
    [InlineData(31, true, true, false, "overdue")]
    [InlineData(31, false, false, false, "overdue")]
    [InlineData(40, true, true, true, null)]
    public void Asama(int day, bool s20, bool s27, bool so, string? expected) =>
        Assert.Equal(expected, DataRequestReminders.Due(C, Due, C.AddDays(day), s20, s27, so));

    [Fact]
    public void Gun_sayaci()
    {
        Assert.Equal(1, DataRequestReminders.DayOf(C, C.AddHours(2)));
        Assert.Equal(20, DataRequestReminders.DayOf(C, C.AddDays(19).AddHours(1)));
        Assert.Equal(31, DataRequestReminders.DayOf(C, C.AddDays(30).AddMinutes(1)));
    }
}

public class DataRequestPackageTests
{
    [Fact]
    public void Zip_icerigi_ve_manifest_ozetleri()
    {
        var bundle = new Dictionary<string, object?> { ["calisan"] = new List<object> { new { Ad = "Ayşe" } }, ["izinTalepleri"] = new List<object>() };
        var zip = DataRequestPackages.BuildZip(bundle, new List<object> { new { action = "Exported" } }, "özet", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        using var archive = new ZipArchive(new MemoryStream(zip));
        var names = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Equal(new HashSet<string> { "kisisel-veri.json", "erisim-kayitlari.json", "basvuru.txt", "BENIOKU.txt", "manifest.json" }, names);
        using var ms = new MemoryStream();
        archive.GetEntry("manifest.json")!.Open().CopyTo(ms);
        var manifest = JsonDocument.Parse(ms.ToArray()).RootElement.GetProperty("files");
        foreach (var f in manifest.EnumerateArray())
        {
            using var e = new MemoryStream();
            archive.GetEntry(f.GetProperty("file").GetString()!)!.Open().CopyTo(e);
            Assert.Equal(f.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(e.ToArray())).ToLowerInvariant());
        }
        var counts = DataRequestPackages.SectionCounts(bundle);
        Assert.Equal(1, counts["calisan"]);
        Assert.Equal(0, counts["izinTalepleri"]);
        Assert.Contains("KVKK m.14", DataRequestPackages.Readme);
    }
}

public class ConsentCampaignTests
{
    private static ConsentCampaigns.LatestConsent L(string v, bool g) => new("u", v, g);

    [Fact]
    public void Hedef_kitle()
    {
        // Aydınlatma: herkes hedef.
        Assert.True(ConsentCampaigns.IsTarget(true, null, "2"));
        // Açık rıza: yalnızca eski sürüme onay vermiş olan yeniden sorulur; ret ya da yanıtsız kişi zorlanmaz.
        Assert.True(ConsentCampaigns.IsTarget(false, L("1", true), "2"));
        Assert.False(ConsentCampaigns.IsTarget(false, L("1", false), "2"));
        Assert.False(ConsentCampaigns.IsTarget(false, null, "2"));
    }

    [Fact]
    public void Tamamlanma()
    {
        Assert.True(ConsentCampaigns.IsDone(false, L("2", false), "2"));   // yeni sürüme ret de bir yanıttır
        Assert.True(ConsentCampaigns.IsDone(true, L("2", true), "2"));
        Assert.False(ConsentCampaigns.IsDone(true, L("1", true), "2"));
        Assert.False(ConsentCampaigns.IsDone(false, null, "2"));
    }

    [Fact]
    public void Ilerleme_hesabi()
    {
        Person P(string id, string? user) => new(Guid.NewGuid(), id, null, null, null, "Müh", new DateOnly(2020, 1, 1), "Active", user, null);
        var people = new[] { P("A", "a"), P("B", "b"), P("C", "c"), P("D", null) };
        var latest = new Dictionary<(string, string), ConsentCampaigns.LatestConsent>
        {
            [("a", "GORSEL_KULLANIM")] = new("a", "2026.1", true),   // eski sürüme onay → bekliyor
            [("b", "GORSEL_KULLANIM")] = new("b", "test-2", false),  // yeni sürüme ret → tamam
            [("c", "GORSEL_KULLANIM")] = new("c", "2026.1", false),  // eski sürüme ret → hedef değil
        };
        var p = ConsentCampaigns.Compute(false, "GORSEL_KULLANIM", "test-2", people, latest);
        Assert.Equal(2, p.Target);
        Assert.Equal(1, p.Done);
        Assert.Equal("A", Assert.Single(p.Pending).Name);
    }
}

public class VerbisInventoryTests
{
    private static VerbisInventory.Item Item(bool active = true, params string[] providers) => new(Guid.NewGuid(), "k", "Catalog", "İzin", "İzin talepleri",
        new[] { "Çalışanlar" }, new[] { "Kimlik (ad, soyad)", "Sağlık" }, "İzin yönetimi", "m.5/2-ç", true, "10 yıl", null,
        Array.Empty<string>(), providers, "Rol bazlı erişim", active, DateTime.UtcNow, "İK");

    [Theory]
    [InlineData("Kimlik (ad, soyad)", "Kimlik", "ad, soyad")]
    [InlineData("Sağlık", "Sağlık", "")]
    [InlineData("Finans (IBAN)", "Finans", "IBAN")]
    public void Kategori_ayirma(string raw, string cat, string detail) =>
        Assert.Equal((cat, detail), VerbisInventory.SplitCategory(raw));

    [Fact]
    public void Satirlar_kategori_basina_ve_pasif_faaliyet_disarida()
    {
        var transfers = new Dictionary<string, VerbisInventory.TransferInfo> { ["slack"] = new("slack", "Slack", "ABD", "Standart sözleşme", true) };
        var rows = VerbisInventory.Rows(new[] { Item(true, "slack"), Item(false) }, transfers);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(VerbisInventory.Columns.Length, r.Length));
        Assert.Equal("Kimlik", rows[0][0]);
        Assert.Contains("Slack (ABD) — Standart sözleşme", rows[0][7]);
        Assert.Equal("Evet", rows[0][8]);
        Assert.Equal("Yok", VerbisInventory.Rows(new[] { Item() }, transfers)[0][7]);
    }

    [Fact]
    public void Csv_kacirma_ve_formul_enjeksiyonu()
    {
        var csv = VerbisInventory.Csv(new[] { new[] { "=HYPERLINK(\"x\")", "a;b", "düz", "", "", "", "", "", "", "", "", "" } });
        Assert.StartsWith("﻿", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
        Assert.Contains("\"a;b\"", csv);
    }

    [Fact]
    public void Html_kacirma()
    {
        var html = VerbisInventory.Html("<Şirket>", new[] { new[] { "<script>", "", "", "", "", "", "", "", "", "", "", "" } }, DateTime.UtcNow, DateTime.UtcNow, "İK", 1);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Son güncelleme", html);
    }

    [Fact]
    public void Dogrulama()
    {
        Assert.Null(VerbisInventory.Validate("İzin", "İzin talepleri", "amaç", "m.5", "10 yıl", new[] { "Kimlik" }, new[] { "Çalışanlar" }));
        Assert.NotNull(VerbisInventory.Validate("İzin", "İzin talepleri", "amaç", "", "10 yıl", new[] { "Kimlik" }, new[] { "Çalışanlar" }));
        Assert.NotNull(VerbisInventory.Validate("İzin", "İzin talepleri", "amaç", "m.5", "10 yıl", new[] { " " }, new[] { "Çalışanlar" }));
        Assert.Equal(new[] { "A", "b" }, VerbisInventory.CleanList(new[] { " A ", "a", "", "b" }));
    }

    [Fact]
    public void Katalog_yeni_ml_faaliyetlerini_icerir()
    {
        foreach (var id in new[] { "attrition-training", "growth-recommendations", "candidate-fit" })
            Assert.Contains(PrivacyCatalog.Activities, a => a.Id == id);
        Assert.Equal(PrivacyCatalog.Activities.Length, PrivacyCatalog.Activities.Select(a => a.Id).Distinct().Count());
    }
}

public class AttritionTrainingTests
{
    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(100.0, 5.0)]
    [InlineData(50.0, 3.0)]
    public void Puan_olcegi(double raw, double expected) => Assert.Equal(expected, AttritionTraining.RatingTo5(raw), 3);

    [Fact]
    public void Hazirlama_kirpar_doldurur_ve_kimlik_icermez()
    {
        var raw = new[]
        {
            new AttritionTraining.RawRow(50, null, null, 500, 300, 900, true),
            new AttritionTraining.RawRow(2, 1.1, 80, 6, 4, 10, false),
        };
        var p = AttritionTraining.Prepare(raw, 1);
        Assert.Equal(2, p.Rows.Count);
        Assert.Equal(1, p.Leavers);
        Assert.Equal(1, p.Imputed["compa_ratio"]);
        Assert.Equal(1, p.Imputed["last_rating"]);
        var big = p.Rows.Single(r => (int)r["label"] == 1);
        Assert.Equal(45.0, big["tenure_years"]);
        Assert.Equal(360.0, big["months_since_promotion"]);
        Assert.Equal(200.0, big["overtime_hours_month"]);
        Assert.Equal(500.0, big["training_hours_year"]);
        Assert.Equal(1.0, big["compa_ratio"]);
        Assert.All(p.Rows, r => Assert.Equal(AttritionTraining.Features.Append("label").OrderBy(x => x), r.Keys.OrderBy(x => x)));
    }

    [Fact]
    public void Esikler()
    {
        AttritionTraining.Prepared Make(int n, int leavers) => new(Enumerable.Range(0, n).Select(_ => new Dictionary<string, object>()).ToList(), leavers, new());
        Assert.Empty(AttritionTraining.Refusals(Make(250, 30), Make(80, 8)));
        Assert.Contains(AttritionTraining.Refusals(Make(150, 30), Make(80, 8)), m => m.Contains("200"));
        Assert.Contains(AttritionTraining.Refusals(Make(250, 10), Make(80, 8)), m => m.Contains("20"));
        Assert.NotEmpty(AttritionTraining.Refusals(Make(250, 30), Make(80, 2)));
        Assert.NotEmpty(AttritionTraining.Refusals(Make(250, 30), Make(30, 8)));
    }

    [Fact]
    public void Zaman_bazli_anlik_goruntuler_ve_kiraci_ozeti()
    {
        var (t1, t2) = AttritionTraining.Snapshots(new DateOnly(2026, 10, 7));
        Assert.Equal(new DateOnly(2024, 10, 7), t1);
        Assert.Equal(new DateOnly(2025, 10, 7), t2);
        var r = AttritionTraining.TenantRef("demo");
        Assert.Equal(16, r.Length);
        Assert.DoesNotContain("demo", r);
        Assert.Equal(r, AttritionTraining.TenantRef("demo"));
        Assert.NotEqual(r, AttritionTraining.TenantRef("acme"));
    }

    [Fact]
    public void Aday_adi_metinden_cikarilir()
    {
        var t = CandidateText.StripName("AYŞE YILMAZ\nAyşe Yılmaz, C# geliştirici. ayse.yilmaz@x.com Yılmazlar A.Ş.", "Ayşe", "Yılmaz", "ayse.yilmaz@x.com");
        Assert.DoesNotContain("Ayşe", t);
        Assert.DoesNotContain("AYŞE", t);
        Assert.DoesNotContain("ayse.yilmaz", t);
        Assert.Contains("C# geliştirici", t);
        Assert.Contains("Yılmazlar", t); // sözcük sınırı: başka sözcüğün parçası silinmez
    }
}
