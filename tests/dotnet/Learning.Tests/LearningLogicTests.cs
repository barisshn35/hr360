using System.IO.Compression;
using System.Text;
using LearningService.Models;
using LearningService.Services;
using Xunit;

namespace Learning.Tests;

/// <summary>Y19 yetkinlik açığı ve öneri sıralaması.</summary>
public class CompetencyMathTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Dept = Guid.NewGuid();

    [Fact]
    public void Pozisyon_tanimi_departmani_gecersiz_kilar_ve_Turkce_harf_farki_eslesir()
    {
        var entries = new[]
        {
            new RoleProfileEntry { CompetencyId = A, DepartmentId = Dept, RequiredLevel = 2 },
            new RoleProfileEntry { CompetencyId = A, PositionTitle = "YAZILIM MÜHENDISI", RequiredLevel = 4 },
            new RoleProfileEntry { CompetencyId = B, DepartmentId = Dept, RequiredLevel = 3 },
            new RoleProfileEntry { CompetencyId = C, PositionTitle = "Muhasebeci", RequiredLevel = 5 },
        };
        var req = CompetencyMath.Requirements(entries, " Yazılım  Mühendisi ", Dept);
        Assert.Equal(2, req.Count);
        Assert.Equal((4, "Position"), req[A]);
        Assert.Equal((3, "Department"), req[B]);
    }

    [Fact]
    public void Son_degerlendirme_gecerli()
    {
        var t = DateTimeOffset.UtcNow;
        var latest = CompetencyMath.Latest(new[]
        {
            new CompetencyAssessment { CompetencyId = A, Level = 2, AssessedAt = t.AddDays(-2), Source = AssessmentSource.Self },
            new CompetencyAssessment { CompetencyId = A, Level = 4, AssessedAt = t, Source = AssessmentSource.Manager },
            new CompetencyAssessment { CompetencyId = A, Level = 1, AssessedAt = t.AddDays(-1), Source = AssessmentSource.Hr },
        });
        Assert.Equal(4, latest[A].Level);
        Assert.Equal(AssessmentSource.Manager, latest[A].Source);
    }

    [Fact]
    public void Acik_hesabi_ve_siralama()
    {
        var req = new Dictionary<Guid, (int, string)> { [A] = (4, "Position"), [B] = (3, "Department"), [C] = (2, "Department") };
        var gaps = CompetencyMath.Gaps(req, new Dictionary<Guid, int> { [A] = 3, [C] = 5 });
        Assert.Equal(new[] { B, A, C }, gaps.Select(g => g.CompetencyId));
        Assert.Equal(3, gaps[0].Gap);
        Assert.Null(gaps[0].Current);           // değerlendirilmemiş
        Assert.Equal(1, gaps[1].Gap);
        Assert.Equal(0, gaps[2].Gap);           // fazla seviye negatif açık değildir
    }

    [Fact]
    public void Oneri_acik_buyuklugune_gore_siralanir_ve_acigi_kapatmayan_elenir()
    {
        var gaps = new List<GapItem>
        {
            new(A, 4, 3, 1, "Position"), new(B, 3, null, 3, "Department"), new(C, 2, 5, 0, "Department"),
        };
        Guid c1 = Guid.NewGuid(), c2 = Guid.NewGuid(), c3 = Guid.NewGuid(), c4 = Guid.NewGuid(), c5 = Guid.NewGuid();
        var tags = new[]
        {
            new CourseCompetency { CourseId = c1, CompetencyId = A, TargetLevel = 4 },
            new CourseCompetency { CourseId = c2, CompetencyId = B, TargetLevel = 2 },
            new CourseCompetency { CourseId = c3, CompetencyId = B, TargetLevel = 5 },
            new CourseCompetency { CourseId = c4, CompetencyId = C, TargetLevel = 5 },   // açık yok
            new CourseCompetency { CourseId = c5, CompetencyId = A, TargetLevel = 3 },   // güncelden yüksek değil
        };
        var recs = CompetencyMath.Recommend(gaps, tags);
        Assert.Equal(new[] { c3, c2, c1 }, recs.Select(r => r.CourseId));
        Assert.Equal(3, recs[0].Closes[0].To);  // beklenenden yukarı taşmaz
        Assert.Equal(0, recs[0].Closes[0].From);
    }
}

/// <summary>Y20 sınav puanlama.</summary>
public class QuizGraderTests
{
    private static readonly Guid Q1 = Guid.NewGuid(), Q2 = Guid.NewGuid();
    private static readonly List<GradableQuestion> Qs = new()
    {
        new(Q1, QuestionKind.Single, new[] { "b" }),
        new(Q2, QuestionKind.Multiple, new[] { "a", "c" }),
    };

    private static Dictionary<Guid, IReadOnlyCollection<string>> Ans(params (Guid, string[])[] a) =>
        a.ToDictionary(x => x.Item1, x => (IReadOnlyCollection<string>)x.Item2);

    [Fact]
    public void Tam_dogru_gecer() =>
        Assert.True(QuizGrader.Grade(Qs, Ans((Q1, new[] { "b" }), (Q2, new[] { "c", "a" })), 100).Passed);

    [Fact]
    public void Coklu_secimde_kismi_puan_yok()
    {
        var g = QuizGrader.Grade(Qs, Ans((Q1, new[] { "b" }), (Q2, new[] { "a" })), 60);
        Assert.Equal(50m, g.ScorePercent);
        Assert.False(g.Passed);
        Assert.False(g.PerQuestion[Q2]);
    }

    [Fact]
    public void Tek_secimde_birden_fazla_isaret_yanlis()
    {
        var g = QuizGrader.Grade(Qs, Ans((Q1, new[] { "a", "b" }), (Q2, new[] { "a", "c" })), 50);
        Assert.False(g.PerQuestion[Q1]);
        Assert.Equal(50m, g.ScorePercent);
        Assert.True(g.Passed);                   // sınırda geçer (>=)
    }

    [Fact]
    public void Yanitsiz_soru_yanlis_ve_bos_sinav_gecmez()
    {
        Assert.Equal(0m, QuizGrader.Grade(Qs, Ans(), 1).ScorePercent);
        Assert.False(QuizGrader.Grade(new List<GradableQuestion>(), Ans(), 0).Passed);
    }

    [Fact]
    public void Kurs_sonucu_karari()
    {
        Assert.Equal(CourseOutcome.NoContent, CourseCompletion.Decide(0, Array.Empty<string>(), false));
        Assert.Equal(CourseOutcome.InProgress, CourseCompletion.Decide(2, new[] { ProgressStatus.Completed }, false));
        Assert.Equal(CourseOutcome.Completed, CourseCompletion.Decide(2, new[] { ProgressStatus.Completed, ProgressStatus.Completed }, false));
        Assert.Equal(CourseOutcome.Failed, CourseCompletion.Decide(2, new[] { ProgressStatus.Completed, ProgressStatus.Failed }, true));
    }

    [Fact]
    public void Dogrulama_kodu_bicimi()
    {
        var c = CertificateCodes.New();
        Assert.Matches("^[2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{4}$", c);
    }
}

/// <summary>Y20 SCORM paket doğrulama ve başlatma jetonu.</summary>
public class ScormTests
{
    private const string Manifest = """
        <?xml version="1.0"?>
        <manifest identifier="M1" xmlns="http://www.imsproject.org/xsd/imscp_rootv1p1p2" xmlns:adlcp="http://www.adlnet.org/xsd/adlcp_rootv1p2">
          <metadata><schemaversion>1.2</schemaversion></metadata>
          <organizations default="O"><organization identifier="O"><title>Ders</title><item identifier="I" identifierref="R"/></organization></organizations>
          <resources><resource identifier="R" adlcp:scormtype="sco" href="sco/start.html?x=1"/></resources>
        </manifest>
        """;

    private static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (n, c) in files)
            {
                using var s = z.CreateEntry(n).Open();
                s.Write(Encoding.UTF8.GetBytes(c));
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Gecerli_paket_baslangic_dosyasi_ve_icerik_turleri()
    {
        var r = ScormPackageReader.Read(Zip(("imsmanifest.xml", Manifest), ("sco/start.html", "<html/>"), ("sco/a.js", "x")));
        Assert.Null(r.Error);
        Assert.Equal("sco/start.html?x=1", r.EntryPoint);
        Assert.Equal("Ders", r.Title);
        Assert.Equal("text/javascript; charset=utf-8", r.Files.Single(f => f.Path == "sco/a.js").ContentType);
    }

    [Theory]
    [InlineData("../x.html")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("C:/win.ini")]
    public void Kacis_yollari_reddedilir(string path) => Assert.Null(ScormPackageReader.SafePath(path));

    [Fact]
    public void Yol_normallestirme() => Assert.Equal("a/b/c.js", ScormPackageReader.SafePath("a\\./b//c.js"));

    [Fact]
    public void Manifestsiz_ve_2004_paketleri_reddedilir()
    {
        Assert.NotNull(ScormPackageReader.Read(Zip(("index.html", "x"))).Error);
        Assert.Contains("1.2", ScormPackageReader.Read(Zip(("imsmanifest.xml", Manifest.Replace(">1.2<", ">2004 4th Edition<")), ("sco/start.html", "x"))).Error);
        Assert.NotNull(ScormPackageReader.Read(Zip(("imsmanifest.xml", Manifest))).Error); // başlangıç dosyası yok
    }

    [Fact]
    public void Dtd_iceren_manifest_reddedilir()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><manifest>&x;</manifest>";
        Assert.NotNull(ScormPackageReader.Read(Zip(("imsmanifest.xml", xxe))).Error);
    }

    [Fact]
    public void Baslatma_jetonu_paket_sure_ve_imza_kontrolu()
    {
        var pkg = Guid.NewGuid();
        var emp = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var t = ScormLaunchTokens.Issue("demo", pkg, emp, now);
        Assert.Equal(("demo", emp), ScormLaunchTokens.Validate(t, pkg, now)!.Value);
        Assert.Null(ScormLaunchTokens.Validate(t, Guid.NewGuid(), now));
        Assert.Null(ScormLaunchTokens.Validate(t, pkg, now.Add(ScormLaunchTokens.Lifetime).AddMinutes(1)));
        Assert.Null(ScormLaunchTokens.Validate(t[..^2] + "AA", pkg, now));
        Assert.Null(ScormLaunchTokens.Validate("garbage", pkg, now));
    }
}

/// <summary>G17 hatırlatma türü.</summary>
public class ReminderPlanTests
{
    [Theory]
    [InlineData(31, null)]
    [InlineData(30, "D30")]
    [InlineData(8, "D30")]
    [InlineData(7, "D7")]
    [InlineData(1, "D7")]
    [InlineData(0, "Expired")]
    [InlineData(-30, "Expired")]
    [InlineData(-31, null)]
    public void Tur(int daysLeft, string? kind) => Assert.Equal(kind, ReminderPlan.KindFor(daysLeft));
}
