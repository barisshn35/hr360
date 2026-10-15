using RecruitmentService.Models;
using RecruitmentService.Services;
using Xunit;

namespace Recruitment.Tests;

public class DuplicateDetectorTests
{
    [Theory]
    [InlineData(" Ali.Veli+is@Example.COM ", "ali.veli@example.com")]
    [InlineData("ayse@x.io", "ayse@x.io")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void NormalizesEmail(string? input, string? expected) => Assert.Equal(expected, DuplicateDetector.NormalizeEmail(input));

    [Theory]
    [InlineData("+90 (532) 111 22 33", "5321112233")]
    [InlineData("0532 111 2233", "5321112233")]
    [InlineData("5321112233", "5321112233")]
    [InlineData("12-34", null)]
    [InlineData(null, null)]
    public void NormalizesPhone(string? input, string? expected) => Assert.Equal(expected, DuplicateDetector.NormalizePhone(input));

    [Fact]
    public void FoldsTurkishNames() => Assert.Equal("isil cagri ozturk", DuplicateDetector.NormalizeName("  IŞIL  Çağrı Öztürk "));

    [Theory]
    [InlineData("Ayşe", "Yılmaz", "Ayse", "Yilmaz", true)]     // harf katlama
    [InlineData("Mehmet", "Demir", "Mehmed", "Demir", true)]   // yazım hatası
    [InlineData("Ali", "Veli", "Veli", "Ali", true)]           // yer değiştirmiş
    [InlineData("Ayşe Nur", "Kaya", "Ayşe", "Kaya", true)]     // ikinci ad
    [InlineData("Zeynep", "Kaya", "Mustafa", "Kaya", false)]   // aile hattı
    public void NameSimilarity(string fa, string la, string fb, string lb, bool expected) =>
        Assert.Equal(expected, DuplicateDetector.NamesSimilar(fa, la, fb, lb));

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly DuplicateDetector.Candidate[] Pool =
    {
        new(A, "Ayşe", "Yılmaz", "ayse@x.com", "5321112233"),
        new(B, "Mustafa", "Kaya", "mk@x.com", "5559998877"),
    };

    [Fact]
    public void EmailMatchIsStrong()
    {
        var m = DuplicateDetector.FindBest(Pool, "Başka", "Ad", "AYSE+cv@x.com", null);
        Assert.NotNull(m);
        Assert.Equal(A, m!.CandidateId);
        Assert.Equal(DuplicateDetector.Strength.Strong, m.Strength);
        Assert.Equal("email", m.Reason);
    }

    [Fact]
    public void PhoneWithSimilarNameIsStrong()
    {
        var m = DuplicateDetector.FindBest(Pool, "Ayse", "Yilmaz", "yeni@x.com", "+90 532 111 22 33");
        Assert.Equal(DuplicateDetector.Strength.Strong, m!.Strength);
        Assert.Equal("phone+name", m.Reason);
    }

    [Fact]
    public void PhoneWithDifferentNameIsOnlyPossible()
    {
        var m = DuplicateDetector.FindBest(Pool, "Zeynep", "Kaya", "z@x.com", "05559998877");
        Assert.Equal(B, m!.CandidateId);
        Assert.Equal(DuplicateDetector.Strength.Possible, m.Strength);
    }

    [Fact]
    public void NoMatch() => Assert.Null(DuplicateDetector.FindBest(Pool, "Yeni", "Aday", "yeni@y.com", "5000000000"));

    [Fact]
    public void EmailBeatsPhone()
    {
        var m = DuplicateDetector.FindBest(Pool, "Ayşe", "Yılmaz", "mk@x.com", "5321112233");
        Assert.Equal(B, m!.CandidateId);
        Assert.Equal("email", m.Reason);
    }
}

public class ScorecardRulesTests
{
    private static readonly List<ScorecardCriterion> Criteria = new()
    {
        new("tech", "Teknik", 3),
        new("comm", "İletişim", 1),
    };

    [Fact]
    public void WeightedAverage()
    {
        // (3*5 + 1*2) / 4 = 4.25
        var (overall, error) = ScorecardRules.Weighted(Criteria, new[] { new CriterionScore("tech", 5), new CriterionScore("comm", 2) });
        Assert.Null(error);
        Assert.Equal(4.25m, overall);
    }

    [Fact]
    public void UnscoredCriteriaIgnored()
    {
        var (overall, _) = ScorecardRules.Weighted(Criteria, new[] { new CriterionScore("comm", 3) });
        Assert.Equal(3m, overall);
    }

    [Fact]
    public void RoundsToTwoDecimals()
    {
        var c = new List<ScorecardCriterion> { new("a", "A", 1), new("b", "B", 1), new("c", "C", 1) };
        var (overall, _) = ScorecardRules.Weighted(c, new[] { new CriterionScore("a", 5), new CriterionScore("b", 4), new CriterionScore("c", 4) });
        Assert.Equal(4.33m, overall);
    }

    [Theory]
    [InlineData("tech", 6)]
    [InlineData("tech", 0)]
    [InlineData("nope", 3)]
    public void RejectsInvalidScores(string key, int score)
    {
        var (overall, error) = ScorecardRules.Weighted(Criteria, new[] { new CriterionScore(key, score) });
        Assert.Null(overall);
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsDuplicateKeyScores() =>
        Assert.NotNull(ScorecardRules.Weighted(Criteria, new[] { new CriterionScore("tech", 3), new CriterionScore("TECH", 4) }).Error);

    [Fact]
    public void EmptyScoresGiveNull() => Assert.Equal((null, null), ScorecardRules.Weighted(Criteria, Array.Empty<CriterionScore>()));

    [Fact]
    public void ValidatesTemplate()
    {
        Assert.Null(ScorecardRules.Validate(ScorecardRules.DefaultCriteria()));
        Assert.NotNull(ScorecardRules.Validate(new List<ScorecardCriterion>()));
        Assert.NotNull(ScorecardRules.Validate(new List<ScorecardCriterion> { new("a", "A", 6) }));
        Assert.NotNull(ScorecardRules.Validate(new List<ScorecardCriterion> { new("a", "A", 1), new("A", "B", 1) }));
    }

    [Fact]
    public void Overlap()
    {
        var t = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.True(ScorecardRules.Overlaps(t, 60, t.AddMinutes(30), 60));
        Assert.False(ScorecardRules.Overlaps(t, 60, t.AddMinutes(60), 30)); // uç uca
        Assert.True(ScorecardRules.Overlaps(t.AddMinutes(-30), 45, t, 60));
    }
}

public class SensitiveNoteDetectorTests
{
    [Theory]
    [InlineData("Aday hamile olduğunu söyledi", "Hamilelik")]
    [InlineData("GEBELİK izni planlıyor", "Hamilelik")]
    [InlineData("Sağlık sorunları var gibi", "Sağlık")]
    [InlineData("Dini görüşleri hakkında konuştuk", "Din / inanç")]
    [InlineData("Mezhebi sorulmadı", "Din / inanç")]
    [InlineData("Siyasi görüşü belirgin", "Siyasi görüş")]
    [InlineData("Sendika üyesi", "Sendika")]
    [InlineData("etnik kökeni", "Etnik köken / ırk")]
    [InlineData("engelli raporu var", "Engellilik")]
    [InlineData("Medeni hali: evli", "Medeni hal")]
    [InlineData("iki çocuğu var", "Çocuk / aile planı")]
    [InlineData("45 yaşında", "Yaş")]
    [InlineData("yaşı biraz büyük", "Yaş")]
    public void Flags(string text, string category) =>
        Assert.Contains(SensitiveNoteDetector.Detect(text), w => w.Category == category);

    [Theory]
    [InlineData("Dinamik ve dinleme becerisi yüksek, problem çözmede çok iyi.")]
    [InlineData("İstanbul'da yaşıyor, yaşam boyu öğrenmeye açık.")]
    [InlineData("Yasin Bey ile teknik mülakat yapıldı; C# ve SQL bilgisi güçlü.")]
    [InlineData("Engellemeleri iyi yönetiyor, takım çalışmasına uygun.")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotFlagRelevantNotes(string? text) => Assert.Empty(SensitiveNoteDetector.Detect(text));

    [Fact]
    public void OneWarningPerCategory()
    {
        var w = SensitiveNoteDetector.Detect("evli, evlilik planı, bekar değil; hamile");
        Assert.Single(w, x => x.Category == "Medeni hal");
        Assert.Equal(2, w.Count);
        Assert.All(w, x => Assert.Contains("KVKK", x.Message));
    }
}

public class OfferRulesTests
{
    [Fact]
    public void RendersPlaceholders()
    {
        var v = OfferRules.Values("Ali Veli", "Yazılımcı", 85000m, "TRY", new DateOnly(2030, 2, 1), null, new DateOnly(2030, 1, 15), "Demo AŞ", new DateOnly(2030, 1, 1));
        var text = OfferRules.Render("{adayAdi} / {pozisyon} / {brutMaas} / {baslangicTarihi} / {sonGecerlilik} / {sirketAdi} / {bilinmeyen}", v);
        Assert.Equal("Ali Veli / Yazılımcı / 85.000,00 TL / 01.02.2030 / 15.01.2030 / Demo AŞ / {bilinmeyen}", text);
    }

    [Fact]
    public void DefaultTemplateIsValid()
    {
        Assert.Null(OfferRules.ValidateTemplate(OfferRules.DefaultTemplate));
        Assert.NotNull(OfferRules.ValidateTemplate("Merhaba {adSoyad}"));
        Assert.NotNull(OfferRules.ValidateTemplate(" "));
    }

    [Theory]
    [InlineData(OfferStatus.PendingApproval, OfferStatus.Approved, true)]
    [InlineData(OfferStatus.PendingApproval, OfferStatus.Sent, false)]  // onaysız gönderilemez
    [InlineData(OfferStatus.Rejected, OfferStatus.Sent, false)]
    [InlineData(OfferStatus.Approved, OfferStatus.Sent, true)]
    [InlineData(OfferStatus.Approved, OfferStatus.Accepted, false)]     // gönderilmeden yanıtlanamaz
    [InlineData(OfferStatus.Sent, OfferStatus.Accepted, true)]
    [InlineData(OfferStatus.Sent, OfferStatus.Declined, true)]
    [InlineData(OfferStatus.Accepted, OfferStatus.Withdrawn, false)]
    public void Transitions(OfferStatus from, OfferStatus to, bool ok) =>
        Assert.Equal(ok, OfferRules.CheckTransition(from, to) is null);
}

public class PipelineAndRetentionTests
{
    [Fact]
    public void StagesAreOrdered() =>
        Assert.Equal(new[] { "Applied", "Screening", "Interview", "Offer", "Hired", "Rejected", "Withdrawn" },
            PipelineRules.Stages.Select(s => s.ToString()));

    [Fact]
    public void MoveRules()
    {
        Assert.Null(PipelineRules.CheckMove(ApplicationStatus.Applied, ApplicationStatus.Interview, false));
        Assert.Null(PipelineRules.CheckMove(ApplicationStatus.Interview, ApplicationStatus.Screening, false)); // geri taşıma serbest
        Assert.NotNull(PipelineRules.CheckMove(ApplicationStatus.Interview, ApplicationStatus.Hired, false));
        Assert.NotNull(PipelineRules.CheckMove(ApplicationStatus.Offer, ApplicationStatus.Hired, true));
        Assert.Null(PipelineRules.CheckMove(ApplicationStatus.Offer, ApplicationStatus.Hired, false));
        Assert.NotNull(PipelineRules.CheckMove(ApplicationStatus.Rejected, ApplicationStatus.Applied, false));
    }

    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoConsentRetentionStartsAtClosure()
    {
        var apps = new[] { new RetentionRules.AppSnapshot(ApplicationStatus.Rejected, T0, T0.AddDays(10), null) };
        Assert.Equal(T0.AddDays(190), RetentionRules.DueAt(T0, false, null, apps, 180, 24));
    }

    [Fact]
    public void ConsentKeepsInPool()
    {
        var apps = new[] { new RetentionRules.AppSnapshot(ApplicationStatus.Applied, T0, null, T0.AddDays(5)) }; // ilan kapandı
        Assert.Equal(T0.AddDays(5).AddMonths(24), RetentionRules.DueAt(T0, true, T0, apps, 180, 24));
    }

    [Fact]
    public void ActiveOrHiredIsNeverDue()
    {
        Assert.Null(RetentionRules.DueAt(T0, false, null, new[] { new RetentionRules.AppSnapshot(ApplicationStatus.Interview, T0, null, null) }, 180, 24));
        Assert.Null(RetentionRules.DueAt(T0, false, null, new[] { new RetentionRules.AppSnapshot(ApplicationStatus.Hired, T0, T0, T0) }, 180, 24));
    }

    [Fact]
    public void ClosureBeforeRecordCreationCounts()
    {
        // Aday kaydı yeni, başvurusu eskiden kapanmış: süre kapanıştan işler (kayıt tarihinden değil).
        var apps = new[] { new RetentionRules.AppSnapshot(ApplicationStatus.Rejected, T0, T0, null) };
        Assert.Equal(T0.AddDays(180), RetentionRules.DueAt(T0.AddDays(100), false, null, apps, 180, 24));
        Assert.Equal(T0.AddDays(100 + 180), RetentionRules.DueAt(T0.AddDays(100), false, null, Array.Empty<RetentionRules.AppSnapshot>(), 180, 24));
    }

    [Fact]
    public void LatestClosureWins()
    {
        var apps = new[]
        {
            new RetentionRules.AppSnapshot(ApplicationStatus.Rejected, T0, T0.AddDays(1), null),
            new RetentionRules.AppSnapshot(ApplicationStatus.Withdrawn, T0, T0.AddDays(50), null),
        };
        Assert.Equal(T0.AddDays(50 + 180), RetentionRules.DueAt(T0, false, null, apps, 180, 24));
    }

    [Fact]
    public void RateLimiterWindow()
    {
        var l = new PublicRateLimiter(2, TimeSpan.FromSeconds(10));
        var now = DateTime.UtcNow;
        Assert.True(l.TryAcquire("ip", now));
        Assert.True(l.TryAcquire("ip", now.AddSeconds(1)));
        Assert.False(l.TryAcquire("ip", now.AddSeconds(2)));
        Assert.True(l.TryAcquire("other", now.AddSeconds(2)));
        Assert.True(l.TryAcquire("ip", now.AddSeconds(11)));
    }
}
