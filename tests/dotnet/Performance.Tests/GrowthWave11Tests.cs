using PerformanceService.Models;
using PerformanceService.Services;
using Xunit;

namespace Performance.Tests;

/// <summary>Dalga 11: kalibrasyon oturumu (79), OKR toplama (80), anonim 360 (81).</summary>
public class GrowthWave11Tests
{
    // ------------------------------------------------------------ 79 kalibrasyon

    [Fact]
    public void Kalibrasyon_tasima_karar_notu_ister()
    {
        Assert.NotNull(CalibrationRules.ValidateMove(2, 2, null));
        Assert.NotNull(CalibrationRules.ValidateMove(2, 2, "kısa"));
        Assert.NotNull(CalibrationRules.ValidateMove(4, 2, "Toplantıda örneklerle gerekçelendirildi"));
        Assert.Null(CalibrationRules.ValidateMove(3, 2, "Toplantıda örneklerle gerekçelendirildi"));
    }

    [Fact]
    public void Kalibrasyon_tum_satirlar_onaylanmadan_sonuclanmaz()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.NotNull(CalibrationRules.FinalizeBlocker(Array.Empty<CalibrationRules.ItemState>()));
        var items = new[] { new CalibrationRules.ItemState(a, 5, 6, true), new CalibrationRules.ItemState(b, 5, 5, false) };
        Assert.Contains("1 onaysız", CalibrationRules.FinalizeBlocker(items));
        items[1] = items[1] with { Confirmed = true };
        Assert.Null(CalibrationRules.FinalizeBlocker(items));
        var changed = CalibrationRules.Changed(items);
        Assert.Single(changed);
        Assert.Equal(a, changed[0].EmployeeId);
    }

    // ------------------------------------------------------------ 80 OKR

    private static OkrNode Goal(Guid parent, decimal? progress, int weight = 100, Guid? emp = null) =>
        new() { Id = Guid.NewGuid(), Kind = "Goal", ParentId = parent, OwnProgress = progress, Weight = weight, EmployeeId = emp ?? Guid.NewGuid() };

    [Fact]
    public void Okr_ilerleme_agirlikli_ortalamayla_yukari_toplanir()
    {
        var company = new OkrNode { Id = Guid.NewGuid(), Kind = "Company" };
        var dept = new OkrNode { Id = Guid.NewGuid(), Kind = "Department", ParentId = company.Id, Weight = 100 };
        var nodes = new List<OkrNode> { company, dept, Goal(dept.Id, 100, 30), Goal(dept.Id, 50, 70), Goal(dept.Id, null, 50) };
        var roots = OkrMath.Build(nodes);
        Assert.Single(roots);
        Assert.Equal(65m, dept.Progress);   // (100*30 + 50*70) / 100; iptal (null) hesaba girmez
        Assert.Equal(65m, company.Progress);
        Assert.Equal(3, company.Employees.Count);
    }

    private static Goal G(GoalStatus st, decimal? target = null, decimal? current = null) =>
        new() { Title = "H", Status = st, TargetValue = target, CurrentValue = current };

    [Fact]
    public void Okr_ilerleme_girilmemis_hedef_yuzde_50_sayilmaz_ve_toplamaya_katilmaz()
    {
        Assert.Null(OkrMath.GoalProgress(G(GoalStatus.Active)));                 // eskiden %50
        Assert.Null(OkrMath.GoalProgress(G(GoalStatus.Active, target: 10)));     // güncel değer yok
        Assert.Null(OkrMath.GoalProgress(G(GoalStatus.Cancelled, 10, 5)));
        Assert.Equal(30m, OkrMath.GoalProgress(G(GoalStatus.Active, 10, 3)));
        Assert.Equal(0m, OkrMath.GoalProgress(G(GoalStatus.Active, 10, 0)));     // girilmiş sıfır ilerleme sayılır
        Assert.Equal(100m, OkrMath.GoalProgress(G(GoalStatus.Achieved)));
        Assert.Equal(0m, OkrMath.GoalProgress(G(GoalStatus.Missed)));
        // Puan hesabındaki varsayım değişmedi (değerlendirme puanı ayrı konu).
        Assert.Equal(50m, ScoreCalculator.GoalAchievement(G(GoalStatus.Active)));

        var dept = new OkrNode { Id = Guid.NewGuid(), Kind = "Department" };
        OkrMath.Build(new[] { dept, Goal(dept.Id, OkrMath.GoalProgress(G(GoalStatus.Active, 10, 8))), Goal(dept.Id, OkrMath.GoalProgress(G(GoalStatus.Active))) });
        Assert.Equal(80m, dept.Progress);   // ilerlemesiz hedef ortalamayı %65'e çekmez

        var empty = new OkrNode { Id = Guid.NewGuid(), Kind = "Department" };
        OkrMath.Build(new[] { empty, Goal(empty.Id, null) });
        Assert.Null(empty.Progress);
    }

    [Fact]
    public void Okr_cocugu_olmayan_amacin_ilerlemesi_yok()
    {
        var company = new OkrNode { Id = Guid.NewGuid(), Kind = "Company" };
        OkrMath.Build(new[] { company });
        Assert.Null(company.Progress);
    }

    [Theory]
    [InlineData("Company", null, true)]
    [InlineData("Company", "Company", false)]
    [InlineData("Department", "Company", true)]
    [InlineData("Department", "Department", false)]
    [InlineData("Goal", "Department", true)]
    [InlineData("Goal", "Company", true)]
    [InlineData("Goal", "Goal", false)]
    public void Okr_baglanti_kurallari(string child, string? parent, bool ok) =>
        Assert.Equal(ok, OkrMath.ValidateParent(child, parent) is null);

    [Fact]
    public void Okr_kucuk_grup_ilerlemesi_calisana_gizlenir_ve_yukari_yayilir()
    {
        var viewer = Guid.NewGuid();
        var company = new OkrNode { Id = Guid.NewGuid(), Kind = "Company" };
        var small = new OkrNode { Id = Guid.NewGuid(), Kind = "Department", ParentId = company.Id };
        var big = new OkrNode { Id = Guid.NewGuid(), Kind = "Department", ParentId = company.Id };
        var nodes = new List<OkrNode> { company, small, big, Goal(small.Id, 80, emp: viewer), Goal(small.Id, 40) };
        for (var i = 0; i < 6; i++) nodes.Add(Goal(big.Id, 50));
        var roots = OkrMath.Build(nodes);
        var hidden = OkrMath.Suppressed(roots, viewer, privileged: false);
        Assert.Contains(small.Id, hidden);       // kendisi dışında 1 kişi
        Assert.DoesNotContain(big.Id, hidden);   // 6 kişi
        Assert.Contains(company.Id, hidden);     // küçük grup değeri ebeveynden geri hesaplanabilirdi
        Assert.Empty(OkrMath.Suppressed(roots, viewer, privileged: true));
    }

    [Fact]
    public void Okr_yalnizca_kendi_hedefi_olan_amac_gizlenmez()
    {
        var viewer = Guid.NewGuid();
        var dept = new OkrNode { Id = Guid.NewGuid(), Kind = "Department" };
        var roots = OkrMath.Build(new List<OkrNode> { dept, Goal(dept.Id, 80, emp: viewer) });
        Assert.Empty(OkrMath.Suppressed(roots, viewer, privileged: false));
    }

    // ------------------------------------------------------------ 81 anonim 360

    private static readonly List<F360Competency> Comps = F360Rules.DefaultCompetencies();

    [Fact]
    public void Ucyuzaltmis_en_az_bes_degerlendiren_ister()
    {
        var subject = Guid.NewGuid();
        var four = Enumerable.Range(0, 4).Select(_ => (Guid.NewGuid(), "Peer")).ToList();
        Assert.NotNull(F360Rules.ValidateReviewers(subject, four));
        var five = four.Append((Guid.NewGuid(), "Manager")).ToList();
        Assert.Null(F360Rules.ValidateReviewers(subject, five));
        Assert.NotNull(F360Rules.ValidateReviewers(subject, five.Append((subject, "Peer")).ToList()));
        Assert.NotNull(F360Rules.ValidateReviewers(subject, five.Append(five[0]).ToList()));
        Assert.NotNull(F360Rules.ValidateReviewers(subject, five.Select(r => (r.Item1, "Boss")).ToList()));
    }

    [Fact]
    public void Ucyuzaltmis_puan_dogrulamasi()
    {
        Assert.NotNull(F360Rules.ValidateRatings(Comps, new Dictionary<string, int>(), null));
        Assert.NotNull(F360Rules.ValidateRatings(Comps, new Dictionary<string, int> { ["x"] = 3 }, null));
        Assert.NotNull(F360Rules.ValidateRatings(Comps, new Dictionary<string, int> { ["communication"] = 6 }, null));
        Assert.Null(F360Rules.ValidateRatings(Comps, new Dictionary<string, int> { ["communication"] = 4 }, "iyi"));
    }

    private static (IReadOnlyDictionary<string, int>, string?) Resp(int c, int? o = null, string? comment = null)
    {
        var d = new Dictionary<string, int> { ["communication"] = c };
        if (o is { } v) d["ownership"] = v;
        return (d, comment);
    }

    [Fact]
    public void Ucyuzaltmis_bes_yanittan_az_ya_da_acik_talepte_sonuc_gizli()
    {
        var four = Enumerable.Range(0, 4).Select(i => Resp(4)).ToList();
        Assert.True(F360Rules.Aggregate(Comps, four, 5, closed: true).Hidden);
        var five = Enumerable.Range(0, 5).Select(i => Resp(4)).ToList();
        Assert.True(F360Rules.Aggregate(Comps, five, 5, closed: false).Hidden);
        var shown = F360Rules.Aggregate(Comps, five, 5, closed: true);
        Assert.False(shown.Hidden);
        Assert.Empty(shown.Comments);
    }

    [Fact]
    public void Ucyuzaltmis_yetkinlik_basina_esik_ve_yorum_sirasi()
    {
        var list = new List<(IReadOnlyDictionary<string, int>, string?)>
        {
            Resp(5, 2, "Zeynep gibi"), Resp(4, 3, "  "), Resp(3, null, "Açık iletişim"), Resp(4, 4), Resp(5, 1, "Başarılı"),
        };
        var r = F360Rules.Aggregate(Comps, list, 5, closed: true);
        var comm = r.Competencies.Single(c => c.Key == "communication");
        Assert.False(comm.Hidden);
        Assert.Equal(4.2m, comm.Average);
        Assert.Equal(new[] { 0, 0, 1, 2, 2 }, comm.Distribution);
        var own = r.Competencies.Single(c => c.Key == "ownership");
        Assert.True(own.Hidden);                 // 4 puan
        Assert.Null(own.Average);
        Assert.Equal(new[] { "Açık iletişim", "Başarılı", "Zeynep gibi" }, r.Comments); // yanıt sırası değil, metin sırası
    }

    [Fact]
    public void Ucyuzaltmis_yetkinlik_dogrulamasi()
    {
        Assert.Null(F360Rules.ValidateCompetencies(Comps));
        Assert.NotNull(F360Rules.ValidateCompetencies(new List<F360Competency>()));
        Assert.NotNull(F360Rules.ValidateCompetencies(new List<F360Competency> { new("A B", "İletişim") }));
        Assert.NotNull(F360Rules.ValidateCompetencies(new List<F360Competency> { new("a", "İletişim"), new("a", "Diğer") }));
    }
}
