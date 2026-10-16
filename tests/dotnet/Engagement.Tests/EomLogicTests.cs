using EngagementService.Infrastructure.Eom;
using Xunit;

namespace Engagement.Tests;

public class EomLogicTests
{
    private static readonly EomConfig C = EomConfig.Default;

    [Fact]
    public void Monthly_schedule_opens_on_start_day_in_turkey_time_and_names_previous_month()
    {
        var s = EomLogic.ScheduleFor(C with { StartDay = 1, OpenHour = 9, VotingDays = 3 }, 2026, 11);
        Assert.Equal("2026-10", s.PeriodKey);
        Assert.Equal(new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc), s.VotingStartsAt);
        Assert.Equal(new DateTime(2026, 11, 4, 6, 0, 0, DateTimeKind.Utc), s.Stage1EndsAt);
        Assert.Null(s.NominationStartsAt);
        Assert.Null(s.Stage2EndsAt);
        Assert.Equal("2026-12", EomLogic.ScheduleFor(C with { StartDay = 1 }, 2027, 1).PeriodKey);
        Assert.Equal("2026-11", EomLogic.ScheduleFor(C with { StartDay = 20 }, 2026, 11).PeriodKey);
    }

    [Fact]
    public void Two_stage_and_nomination_windows_follow_each_other()
    {
        var cfg = C with { Scope = Eom.TwoStage, Process = Eom.Nominate, NominationDays = 2, VotingDays = 1, Stage2Days = 1 };
        var s = EomLogic.ScheduleFor(cfg, 2026, 11);
        Assert.Equal(s.VotingStartsAt.AddDays(-2), s.NominationStartsAt);
        Assert.Equal(s.VotingStartsAt.AddDays(1), s.Stage1EndsAt);
        Assert.Equal(s.Stage1EndsAt.AddDays(1), s.Stage2EndsAt);
        Assert.Equal(s.NominationStartsAt, s.FirstStart);
        Assert.Equal(s.Stage2EndsAt, s.FinalEnd);
    }

    [Fact]
    public void Next_schedule_skips_a_vote_that_already_started_and_quarterly_uses_quarter_months()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-11", EomLogic.NextSchedule(C with { StartDay = 1 }, now).PeriodKey); // 1 Aralık'ta açılan, Kasım
        var q = EomLogic.NextSchedule(C with { Cadence = Eom.Quarterly, StartDay = 5 }, now);
        Assert.Equal("2026-Q4", q.PeriodKey);
        Assert.Equal(1, (q.VotingStartsAt + Eom.LocalOffset).Month);
    }

    private static readonly Guid Tech = Guid.NewGuid(), Soft = Guid.NewGuid(), Infra = Guid.NewGuid(), Fin = Guid.NewGuid(), Hr = Guid.NewGuid();

    private static (List<EomDept>, List<EomMember>, Dictionary<string, Guid[]>) Org()
    {
        var depts = new List<EomDept>
        {
            new(Tech, null, "Teknoloji", null), new(Soft, Tech, "Yazılım", null), new(Infra, Tech, "Altyapı", null),
            new(Fin, null, "Finans", null), new(Hr, null, "İK", null),
        };
        Guid[] P(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
        var g = new Dictionary<string, Guid[]> { ["tech"] = P(2), ["soft"] = P(5), ["infra"] = P(1), ["fin"] = P(3), ["hr"] = P(1) };
        var m = g["tech"].Select(x => new EomMember(x, Tech)).Concat(g["soft"].Select(x => new EomMember(x, Soft)))
            .Concat(g["infra"].Select(x => new EomMember(x, Infra))).Concat(g["fin"].Select(x => new EomMember(x, Fin)))
            .Concat(g["hr"].Select(x => new EomMember(x, Hr))).ToList();
        return (depts, m, g);
    }

    [Fact]
    public void Top_level_units_include_sub_departments_and_skip_one_person_units()
    {
        var (d, m, _) = Org();
        var u = EomLogic.BuildUnits(Eom.TopLevel, d, m);
        Assert.Equal(["Finans", "Teknoloji"], u.Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(8, u.Single(x => x.Name == "Teknoloji").Members.Count);
        Assert.All(u, x => Assert.Equal(1, x.Stage));
    }

    [Fact]
    public void Each_department_uses_only_direct_members()
    {
        var (d, m, _) = Org();
        var u = EomLogic.BuildUnits(Eom.EachDepartment, d, m);
        Assert.Equal(["Finans", "Teknoloji", "Yazılım"], u.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, u.Single(x => x.Name == "Teknoloji").Members.Count);
    }

    [Fact]
    public void Two_stage_builds_sub_units_under_a_final_unit_and_keeps_childless_roots_single_stage()
    {
        var (d, m, g) = Org();
        var u = EomLogic.BuildUnits(Eom.TwoStage, d, m);
        var final = u.Single(x => x.Stage == 2);
        Assert.Equal(Tech, final.DepartmentId);
        Assert.Equal(8, final.Members.Count);
        var subs = u.Where(x => x.ParentKey == final.Key).ToList();
        Assert.Equal(3, subs.Count); // doğrudan bağlılar + Yazılım + Altyapı (tek kişi: doğrudan finale)
        Assert.Contains(subs, x => x.DepartmentId == Tech && x.Members.SequenceEqual(g["tech"]));
        Assert.Single(subs.Single(x => x.DepartmentId == Infra).Members);
        Assert.Contains(u, x => x.Name == "Finans" && x.Stage == 1 && x.ParentKey is null);
        Assert.DoesNotContain(u, x => x.Name == "İK");
    }

    [Fact]
    public void Tally_is_weighted_and_breaks_ties_by_raw_votes_then_reports_tie()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        // a: 1 yönetici oyu (2), b: 2 çalışan oyu (1+1) → puan eşit, ham oy b'de fazla → b kazanır
        var t = EomLogic.Tally([new(a, 2m), new(b, 1m), new(b, 1m)]);
        Assert.Equal(b, t.Winner);
        Assert.Equal(2m, t.Ranking[0].Score);
        // a: 2 (yönetici), b: 1 → a kazanır (katsayı belirleyici)
        Assert.Equal(a, EomLogic.Tally([new(a, 2m), new(b, 1m)]).Winner);
        var tie = EomLogic.Tally([new(a, 1m), new(b, 1m)]);
        Assert.Null(tie.Winner);
        Assert.Equal(2, tie.Tied.Count);
        Assert.Null(EomLogic.Tally([]).Winner);
    }

    [Fact]
    public void Finalists_take_top_n_with_ties_at_the_cutoff_and_fall_back_to_everyone()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        var eligible = new[] { a, b, c, d };
        var f = EomLogic.Finalists([a, a, a, b, b, c, c, d], eligible, 2);
        Assert.Equal(3, f.Count); // a (3), b ve c (2'de eşit)
        Assert.DoesNotContain(d, f);
        Assert.Equal(4, EomLogic.Finalists([], eligible, 2).Count);
        Assert.DoesNotContain(Guid.Empty, EomLogic.Finalists([Guid.Empty, a], eligible, 2));
    }

    [Fact]
    public void Weight_is_the_highest_applicable_group()
    {
        var cfg = C with { WeightEmployee = 1m, WeightManager = 3m, WeightHr = 2m };
        Assert.Equal(1m, EomLogic.Weight(cfg, false, false));
        Assert.Equal(3m, EomLogic.Weight(cfg, true, false));
        Assert.Equal(2m, EomLogic.Weight(cfg, false, true));
        Assert.Equal(3m, EomLogic.Weight(cfg, true, true));
    }

    [Fact]
    public void Secret_modes_hash_the_voter_with_the_period_salt_and_identity_modes_keep_the_id()
    {
        Guid unit = Guid.NewGuid(), voter = Guid.NewGuid();
        var salt = new byte[32]; salt[0] = 1;
        var k1 = EomLogic.VoterKey(C, salt, unit, voter);
        Assert.Equal(64, k1.Length);
        Assert.DoesNotContain(voter.ToString("N"), k1);
        Assert.Equal(k1, EomLogic.VoterKey(C, salt, unit, voter));
        Assert.NotEqual(k1, EomLogic.VoterKey(C, new byte[32], unit, voter));
        Assert.Equal(voter.ToString("N"), EomLogic.VoterKey(C with { Visibility = Eom.RevealAfter }, null, unit, voter));
        Assert.Throws<InvalidOperationException>(() => EomLogic.VoterKey(C, null, unit, voter));
    }

    [Fact]
    public void Validation_rejects_out_of_range_settings()
    {
        Assert.Null(C.Validate());
        Assert.NotNull((C with { StartDay = 31 }).Validate());
        Assert.NotNull((C with { Visibility = "Everyone" }).Validate());
        Assert.NotNull((C with { WeightManager = 0m }).Validate());
        Assert.NotNull((C with { Process = Eom.Nominate, NominationDays = 14, VotingDays = 14 }).Validate());
    }

    [Fact]
    public void Last_call_is_a_day_before_close_or_half_way_for_short_votes()
    {
        var start = new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal(start.AddDays(2), EomLogic.LastCallAt(start, start.AddDays(3)));
        Assert.Equal(start.AddHours(12), EomLogic.LastCallAt(start, start.AddDays(1)));
    }

    [Fact]
    public void Leaderboard_shares_rank_on_equal_wins_and_orders_by_wins_then_recency()
    {
        EngagementService.Controllers.EomController.PersonDto P(string n) => new(Guid.NewGuid(), n, null, null, null);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = EngagementService.Controllers.EomController.Rank([
            (P("Ali"), 1, 0, "2026-01", t0), (P("Can"), 3, 0, "2026-02", t0.AddMonths(1)),
            (P("Ece"), 1, 0, "2026-03", t0.AddMonths(2)), (P("Naz"), 0, 2, "2026-04", t0.AddMonths(3)),
        ]);
        Assert.Equal(["Can", "Ece", "Ali", "Naz"], rows.Select(r => r.Person.Name));
        Assert.Equal([1, 2, 2, 4], rows.Select(r => r.Rank));
    }
}
