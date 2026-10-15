using RecruitmentService.Controllers;
using RecruitmentService.Models;
using RecruitmentService.Services;
using Xunit;

namespace Recruitment.Tests;

/// <summary>Dalga 11: öneri, durum bağlantısı, JSON-LD, puan kartı tutarlılığı ve huni analizi kuralları.</summary>
public class RecruitmentW11Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------ öneri (73)

    [Theory]
    [InlineData(ApplicationStatus.Applied, false, "InReview")]
    [InlineData(ApplicationStatus.Interview, false, "InReview")]
    [InlineData(ApplicationStatus.Offer, false, "InReview")]
    [InlineData(ApplicationStatus.Hired, true, "Hired")]
    [InlineData(ApplicationStatus.Rejected, false, "Closed")]
    [InlineData(ApplicationStatus.Withdrawn, false, "Closed")]
    [InlineData(ApplicationStatus.Screening, true, "Closed")]
    public void Referrer_sees_only_coarse_status(ApplicationStatus s, bool closed, string expected) =>
        Assert.Equal(expected, ReferralRules.CoarseStatus(s, closed).Code);

    [Fact]
    public void Deleted_candidate_is_closed_for_referrer() =>
        Assert.Equal("Closed", ReferralRules.CoarseStatus(null, false).Code);

    [Fact]
    public void Reward_waits_for_probation_then_becomes_eligible()
    {
        var hired = T0;
        var (s1, h1, e1) = ReferralRules.Step(ReferralRewardStatus.None, ApplicationStatus.Hired, hired, 60, T0.AddDays(10), null);
        Assert.Equal(ReferralRewardStatus.Waiting, s1);
        Assert.Equal(hired, h1);
        Assert.Equal(hired.AddDays(60), e1);
        var (s2, _, _) = ReferralRules.Step(s1, ApplicationStatus.Hired, hired, 60, T0.AddDays(61), true);
        Assert.Equal(ReferralRewardStatus.Eligible, s2);
        // Deneme süresinde ayrıldıysa hak edilmez (İK düşürür; otomatik karar yok).
        var (s3, _, _) = ReferralRules.Step(s1, ApplicationStatus.Hired, hired, 60, T0.AddDays(61), false);
        Assert.Equal(ReferralRewardStatus.Waiting, s3);
    }

    [Fact]
    public void Reward_probation_counts_from_start_date_not_offer_acceptance()
    {
        var accepted = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);   // "İşe alındı" anı (teklif kabulü)
        var start = new DateOnly(2026, 3, 2);                                       // işe başlama
        var tr = TimeSpan.FromHours(3);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, tr), ReferralRules.ProbationStart(null, start, accepted));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 0, 0, 0, tr), ReferralRules.ProbationStart(new DateOnly(2026, 3, 9), start, accepted)); // çalışan kaydı öncelikli
        Assert.Equal(accepted, ReferralRules.ProbationStart(null, null, accepted));
        Assert.Null(ReferralRules.ProbationStart(null, null, null));

        // Kabulden 61 gün sonra: kabulden sayılsaydı hak edilirdi; başlama tarihinden (2 Mart) sayılınca bekler.
        var probStart = ReferralRules.ProbationStart(null, start, accepted);
        var (s, h, e) = ReferralRules.Step(ReferralRewardStatus.None, ApplicationStatus.Hired, probStart, 60, accepted.AddDays(61), true);
        Assert.Equal(ReferralRewardStatus.Waiting, s);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, tr), e);
        Assert.Equal(ReferralRewardStatus.Eligible, ReferralRules.Step(s, ApplicationStatus.Hired, h, 60, new DateTimeOffset(2026, 5, 1, 0, 0, 0, tr), true).Status);
    }

    [Fact]
    public void Reward_hr_decisions_are_never_changed_automatically()
    {
        foreach (var st in new[] { ReferralRewardStatus.Approved, ReferralRewardStatus.Paid, ReferralRewardStatus.Forfeited, ReferralRewardStatus.NotEligible })
            Assert.Equal(st, ReferralRules.Step(st, ApplicationStatus.Rejected, null, 60, T0, null).Status);
        Assert.Equal(ReferralRewardStatus.None, ReferralRules.Step(ReferralRewardStatus.None, ApplicationStatus.Interview, null, 60, T0, null).Status);
    }

    [Fact]
    public void Reward_decision_transitions()
    {
        Assert.Equal(ReferralRewardStatus.Approved, ReferralRules.Decide(ReferralRewardStatus.Eligible, "approve").Next);
        Assert.NotNull(ReferralRules.Decide(ReferralRewardStatus.Waiting, "approve").Error);
        Assert.Equal(ReferralRewardStatus.Paid, ReferralRules.Decide(ReferralRewardStatus.Approved, "pay").Next);
        Assert.NotNull(ReferralRules.Decide(ReferralRewardStatus.Eligible, "pay").Error);
        Assert.Equal(ReferralRewardStatus.Forfeited, ReferralRules.Decide(ReferralRewardStatus.Waiting, "forfeit").Next);
        Assert.NotNull(ReferralRules.Decide(ReferralRewardStatus.Paid, "not-eligible").Error);
        Assert.NotNull(ReferralRules.Decide(ReferralRewardStatus.Eligible, "bogus").Error);
    }

    // ------------------------------------------------------------ durum bağlantısı (74)

    [Fact]
    public void Status_link_view_and_next_step()
    {
        Assert.Equal("Received", StatusLinkRules.View(ApplicationStatus.Applied, false, null, null).Code);
        Assert.Contains("mülakat", StatusLinkRules.View(ApplicationStatus.Interview, false, T0, null).NextStep);
        Assert.Equal("Offer", StatusLinkRules.View(ApplicationStatus.Offer, false, null, OfferStatus.Sent).Code);
        Assert.Contains("yanıtlamanız", StatusLinkRules.View(ApplicationStatus.Offer, false, null, OfferStatus.Sent).NextStep);
        Assert.Equal("Closed", StatusLinkRules.View(ApplicationStatus.Screening, true, null, null).Code);
        Assert.Equal("Positive", StatusLinkRules.View(ApplicationStatus.Hired, true, null, OfferStatus.Accepted).Code);
        Assert.Equal("Negative", StatusLinkRules.View(ApplicationStatus.Rejected, false, null, null).Code);
    }

    [Fact]
    public void Status_link_active_only_until_revoked_or_expired()
    {
        Assert.True(StatusLinkRules.IsActive(null, T0.AddDays(1), T0));
        Assert.False(StatusLinkRules.IsActive(T0, T0.AddDays(1), T0));
        Assert.False(StatusLinkRules.IsActive(null, T0, T0));
    }

    [Fact]
    public void Status_rate_limiter_is_separate_and_wider()
    {
        var l = new StatusRateLimiter();
        Assert.True(l.Limit >= 10);
        for (var i = 0; i < l.Limit; i++) Assert.True(l.TryAcquire("ip", T0.UtcDateTime));
        Assert.False(l.TryAcquire("ip", T0.UtcDateTime));
    }

    // ------------------------------------------------------------ JSON-LD (75)

    private static JobPosting Posting() => new()
    {
        Title = "Backend <Geliştirici>", Description = "Satır 1\nSatır 2\n\nParagraf 2", EmploymentType = EmploymentType.FullTime,
        PublishedAt = T0, ValidThrough = T0.AddDays(30), Location = "İstanbul", SalaryMin = 80000, SalaryMax = 120000, SalaryCurrency = "TRY", SalaryPeriod = "MONTH",
    };

    [Fact]
    public void JsonLd_has_required_google_fields()
    {
        var o = JobPostingJsonLd.Build(Posting(), "Demo A.Ş.", null, false, "https://x/kariyer/demo/ilan/1");
        Assert.Equal("JobPosting", (string?)o["@type"]);
        Assert.Equal("2026-01-01", (string?)o["datePosted"]);
        Assert.Equal("2026-01-31T09:00:00Z", (string?)o["validThrough"]);
        Assert.Equal("FULL_TIME", (string?)o["employmentType"]);
        Assert.Equal("Demo A.Ş.", (string?)o["hiringOrganization"]!["name"]);
        Assert.Equal("İstanbul", (string?)o["jobLocation"]!["address"]!["addressLocality"]);
        Assert.Equal("TR", (string?)o["jobLocation"]!["address"]!["addressCountry"]);
        Assert.Equal("<p>Satır 1<br>Satır 2</p><p>Paragraf 2</p>", (string?)o["description"]);
        Assert.Null(o["baseSalary"]); // kiracı açmadıkça ücret yok
    }

    [Fact]
    public void JsonLd_salary_only_when_tenant_opts_in()
    {
        var o = JobPostingJsonLd.Build(Posting(), "Demo", null, true, null);
        Assert.Equal("TRY", (string?)o["baseSalary"]!["currency"]);
        Assert.Equal(80000m, (decimal?)o["baseSalary"]!["value"]!["minValue"]);
        Assert.Equal("MONTH", (string?)o["baseSalary"]!["value"]!["unitText"]);
        var p = Posting(); p.SalaryMin = null; p.SalaryMax = null;
        Assert.Null(JobPostingJsonLd.Build(p, "Demo", null, true, null)["baseSalary"]);
    }

    [Fact]
    public void JsonLd_escapes_html_and_remote()
    {
        var p = Posting(); p.Description = "<script>alert(1)</script>"; p.RemoteAllowed = true;
        var o = JobPostingJsonLd.Build(p, "Demo", null, false, null);
        Assert.DoesNotContain("<script>", (string?)o["description"]);
        Assert.Equal("TELECOMMUTE", (string?)o["jobLocationType"]);
    }

    [Fact]
    public void Career_details_validation()
    {
        Assert.Null(JobPostingJsonLd.Validate(1, 2, "TRY", "MONTH", "TR"));
        Assert.NotNull(JobPostingJsonLd.Validate(5, 2, null, null, null));
        Assert.NotNull(JobPostingJsonLd.Validate(null, null, "XYZ", null, null));
        Assert.NotNull(JobPostingJsonLd.Validate(null, null, null, "DECADE", null));
        Assert.NotNull(JobPostingJsonLd.Validate(null, null, null, null, "TUR"));
    }

    // ------------------------------------------------------------ tutarlılık (77)

    private static readonly List<ScorecardCriterion> Crit = new() { new("tech", "Teknik", 3), new("comm", "İletişim", 1) };

    [Fact]
    public void Consistency_flags_high_disagreement()
    {
        var r = ScorecardConsistency.Compute(Crit, new List<(IReadOnlyList<CriterionScore>, string?)>
        {
            (new List<CriterionScore> { new("tech", 5), new("comm", 3) }, "Yes"),
            (new List<CriterionScore> { new("tech", 1), new("comm", 4) }, "Yes"),
        });
        var tech = r.Criteria.Single(c => c.Key == "tech");
        Assert.Equal(4, tech.Spread);
        Assert.Equal(2m, tech.Std);
        Assert.True(tech.HighDisagreement);
        Assert.False(r.Criteria.Single(c => c.Key == "comm").HighDisagreement);
        Assert.True(r.HighDisagreement);
        Assert.False(r.RecommendationSplit);
    }

    [Fact]
    public void Consistency_single_rater_and_recommendation_split()
    {
        var one = ScorecardConsistency.Compute(Crit, new List<(IReadOnlyList<CriterionScore>, string?)> { (new List<CriterionScore> { new("tech", 5) }, "Yes") });
        Assert.False(one.HighDisagreement);
        Assert.Equal(0, one.Criteria.Single(c => c.Key == "comm").Count);
        var split = ScorecardConsistency.Compute(Crit, new List<(IReadOnlyList<CriterionScore>, string?)>
        {
            (new List<CriterionScore> { new("tech", 4) }, "StrongYes"),
            (new List<CriterionScore> { new("tech", 3) }, "No"),
        });
        Assert.True(split.RecommendationSplit);
        Assert.True(split.HighDisagreement);
    }

    [Fact]
    public void Evidence_is_optional_and_weighted_score_unchanged()
    {
        var (overall, err) = ScorecardRules.Weighted(Crit, new List<CriterionScore> { new("tech", 5, "Sistem tasarımını iyi anlattı"), new("comm", 1) });
        Assert.Null(err);
        Assert.Equal(4m, overall);
    }

    // ------------------------------------------------------------ huni (78)

    private static FunnelAnalytics.App App(int i, ApplicationStatus st, string source, params (string To, int Day)[] ev) =>
        new(Guid.NewGuid(), T0, st, source,
            new[] { ((string?)null, "Applied", T0) }.Concat(ev.Select(e => ((string?)"x", e.To, T0.AddDays(e.Day)))).ToList());

    [Fact]
    public void Funnel_suppresses_small_groups()
    {
        var apps = new List<FunnelAnalytics.App>();
        for (var i = 0; i < 6; i++) apps.Add(App(i, ApplicationStatus.Screening, "Career", ("Screening", 2)));
        for (var i = 0; i < 2; i++) apps.Add(App(i, ApplicationStatus.Hired, "Referral", ("Screening", 1), ("Interview", 5), ("Offer", 10), ("Hired", 20)));
        var r = FunnelAnalytics.Compute(apps, new List<FunnelAnalytics.OfferRow> { new(OfferStatus.Accepted), new(OfferStatus.Accepted) }, T0.AddDays(30));
        Assert.Equal(8, r.Applications);
        Assert.Equal(8, r.Stages[0].Reached);
        Assert.Equal(8, r.Stages[1].Reached);
        Assert.Equal(2, r.Stages[4].Reached);
        Assert.Equal(25.0, r.Stages[2].ConversionFromPrevious);
        // 2 işe alım < 5 → işe alım süresi gizli
        Assert.True(r.TimeToHire.Suppressed);
        Assert.Null(r.TimeToHire.MedianDays);
        // Öneri kaynağı 2 başvuru → gizli; kariyer 6 → görünür
        Assert.True(r.Sources.Single(s => s.Source == "Referral").Suppressed);
        Assert.Equal(6, r.Sources.Single(s => s.Source == "Career").Applications);
        Assert.True(r.Offers.Suppressed);
        Assert.Null(r.Offers.AcceptanceRate);
        // Ön elemede geçen süre: yalnızca 2 tamamlanmış (öneri) → gizli
        Assert.True(r.Stages[1].TimeInStage.Suppressed);
    }

    [Fact]
    public void Funnel_time_to_hire_and_stage_durations()
    {
        var apps = Enumerable.Range(0, 5).Select(i => App(i, ApplicationStatus.Hired, "Career",
            ("Screening", 2), ("Interview", 4 + i), ("Offer", 10), ("Hired", 10 + i * 2))).ToList();
        var offers = new List<FunnelAnalytics.OfferRow> { new(OfferStatus.Accepted), new(OfferStatus.Accepted), new(OfferStatus.Accepted), new(OfferStatus.Declined), new(OfferStatus.Expired) };
        var r = FunnelAnalytics.Compute(apps, offers, T0.AddDays(60));
        Assert.False(r.TimeToHire.Suppressed);
        Assert.Equal(14.0, r.TimeToHire.MedianDays);
        Assert.Equal(2.0, r.Stages[0].TimeInStage.MedianDays); // Applied → Screening 2 gün
        Assert.Equal(60.0, r.Offers.AcceptanceRate);
        Assert.Equal(5, r.Offers.Sent);
    }

    [Fact]
    public void Quantile_interpolates() =>
        Assert.Equal(2.5, FunnelAnalytics.Quantile(new List<double> { 1, 2, 3, 4 }, 0.5));

    [Fact]
    public void Source_label_keys()
    {
        Assert.Equal("Career", RecruitmentAnalyticsController.SourceLabel("Career", "x"));
        Assert.Equal("Referral", RecruitmentAnalyticsController.SourceLabel("Referral", null));
        Assert.Equal("LinkedIn", RecruitmentAnalyticsController.SourceLabel("Manual", " LinkedIn "));
        Assert.Equal("Other", RecruitmentAnalyticsController.SourceLabel("Manual", null));
    }
}
