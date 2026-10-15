using System.Text.Json;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>ML dalgası 2: izin tahmini girdisi, anlamsal arama külliyat anahtarı ve görünürlük, beceri haritası girdisi.</summary>
public class MlInsightsTests
{
    static readonly Guid Eng = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid Fin = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    static Person P(string name, Guid? dept, string? deptName) =>
        new(Guid.NewGuid(), name, null, null, dept, deptName, new DateOnly(2020, 1, 1), "Active", null, null);

    static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public void Izin_tahmini_girdisi_kimliksiz_ve_departman_bazinda()
    {
        var people = new List<Person> { P("A", Eng, "Mühendislik"), P("B", Eng, "Mühendislik"), P("C", Fin, "Finans"), P("D", null, null) };
        var start = new DateOnly(2025, 1, 1);
        var end = new DateOnly(2026, 10, 5);
        var days = new[]
        {
            new LeaveForecastPayload.DayDept(new DateOnly(2026, 9, 1), Eng, 2),
            new LeaveForecastPayload.DayDept(new DateOnly(2026, 9, 1), Fin, 1),
            new LeaveForecastPayload.DayDept(new DateOnly(2026, 9, 2), null, 1),
            new LeaveForecastPayload.DayDept(new DateOnly(2024, 1, 1), Eng, 5),   // aralık dışı
        };
        var body = Json(LeaveForecastPayload.Build(start, end, people, days, new[] { new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 29) }, 40));
        Assert.Equal(4, body.GetProperty("headcount").GetInt32());
        Assert.Equal(26, body.GetProperty("weeks").GetInt32());
        Assert.Single(body.GetProperty("holidays").EnumerateArray());
        var company = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(2, company.Count);
        Assert.Equal(3, company[0].GetProperty("absent").GetInt64());
        var teams = body.GetProperty("teams").EnumerateArray().ToDictionary(t => t.GetProperty("team").GetString()!);
        Assert.Equal(2, teams["Mühendislik"].GetProperty("headcount").GetInt32());
        Assert.Equal(1, teams[LeaveForecastPayload.NoDepartment].GetProperty("days").GetArrayLength());
        Assert.DoesNotContain(people[0].Id.ToString(), body.GetRawText());
    }

    [Fact]
    public void Kulliyat_anahtari_ml_ile_ayni()
    {
        // Değerler ml-inference semantic_search.corpus_key / doc_hash ve Postgres sha256 ile hesaplandı.
        Assert.Equal("8bd0b9fa633e8c7f072901e9e6733c57bff1173cd830709c373c941983bf984f",
            SemanticCorpus.DocHash("İzin politikası", "Yıllık izin ğüşçö"));
        var docs = new[] { ("lib:2", SemanticCorpus.DocHash("T", "x")), ("kb:1", SemanticCorpus.DocHash("İzin politikası", "Yıllık izin ğüşçö")) };
        Assert.Equal("0beeb687836b787c2df3eb5a4cc13060bdae9c715507e3e643e0b36abb1012ec", SemanticCorpus.CorpusKey(docs));
        Assert.Equal(SemanticCorpus.CorpusKey(docs), SemanticCorpus.CorpusKey(docs.Reverse()));
    }

    static SemanticCorpus.Doc D(string source, string audience = "All", Guid[]? depts = null, bool archived = false) =>
        new($"{source}:x", source, Guid.NewGuid(), "t", "", "h", audience, depts ?? Array.Empty<Guid>(), archived);

    [Fact]
    public void Gorunurluk_kutuphane_ve_duyuru_kurallariyla_ayni()
    {
        Assert.True(SemanticCorpus.Visible(D("kb"), false, false, null));
        Assert.False(SemanticCorpus.Visible(D("library", "Managers"), false, false, Eng));
        Assert.True(SemanticCorpus.Visible(D("library", "Managers"), false, true, Eng));
        Assert.True(SemanticCorpus.Visible(D("library", "Departments", new[] { Eng }), false, false, Eng));
        Assert.False(SemanticCorpus.Visible(D("library", "Departments", new[] { Eng }), false, false, Fin));
        Assert.False(SemanticCorpus.Visible(D("library", archived: true), false, true, Eng));
        Assert.True(SemanticCorpus.Visible(D("library", archived: true), true, true, null));
        Assert.False(SemanticCorpus.Visible(D("announcement", "Departments", new[] { Eng }), false, true, null));
        Assert.True(SemanticCorpus.Visible(D("announcement", "Departments", new[] { Eng }), true, true, null));
        Assert.False(SemanticCorpus.Visible(D("other"), true, true, null));
    }

    [Fact]
    public void Beceri_haritasi_girdisi_kimliksiz_ve_tekilsiz()
    {
        var a = P("A", Eng, "Mühendislik");
        var b = P("B", Fin, "Finans");
        var body = Json(SkillGraphPayload.Build(new List<Person> { a, b },
            new Dictionary<Guid, string[]> { [a.Id] = new[] { "Python", " python ", "" } },
            new Dictionary<Guid, List<string>> { [a.Id] = new() { "Sunum becerisi" } }));
        var rows = body.GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(new[] { "Python", "Sunum becerisi" }, rows[0].GetProperty("skills").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(0, rows[1].GetProperty("skills").GetArrayLength());
        Assert.DoesNotContain(a.Id.ToString(), body.GetRawText());
        Assert.DoesNotContain("\"A\"", body.GetRawText());
    }
}
