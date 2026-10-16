using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace EngagementService.Infrastructure.Eom;

public sealed record EomPeriodRow(
    Guid Id, string Key, string Status, EomConfig Config, DateTime? NominationStartsAt, DateTime VotingStartsAt,
    DateTime Stage1EndsAt, DateTime? Stage2EndsAt, byte[]? Salt, DateTime? UpcomingSentAt, DateTime? OpenedSentAt,
    DateTime? VotingSentAt, DateTime? LastCallSentAt, DateTime? Stage2SentAt, DateTime? AnnouncedAt, DateTime CreatedAt, DateTime? ClosedAt)
{
    public DateTime FirstStart => NominationStartsAt ?? VotingStartsAt;
}

public sealed record EomUnitRow(
    Guid Id, Guid PeriodId, int Stage, Guid DepartmentId, string Name, Guid? ParentUnitId, Guid? HeadId,
    Guid[] Members, Guid[] Candidates, string Status, Guid? Winner, decimal? WinnerScore, Guid[] Tied, DateTime? ClosedAt)
{
    /// <summary>Ana departmana doğrudan bağlı kişilerin 1. aşama birimi (iki aşamalı kip).</summary>
    public bool Direct(IEnumerable<EomUnitRow> all) => Stage == 1 && ParentUnitId is { } p && all.Any(u => u.Id == p && u.DepartmentId == DepartmentId);
}

/// <summary>
/// "Ayın çalışanı" dönem yaşam döngüsü (SQL ile; oylar denetim kaydına girmez):
/// Scheduled → (Nominating) → Voting → (Stage2) → Closed (ya da beraberlikte Tie → İK çözer → Closed).
/// Geçişler <see cref="TickAsync"/> ile zamana göre yapılır (işçi ve İK eylemleri çağırır); her geçiş
/// durum koşullu UPDATE ile "sahiplenilir", aynı geçiş iki kez işlemez.
/// </summary>
public sealed class EomStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Sql _sql;
    private readonly PeopleDirectory _people;
    private readonly Notifier _notify;
    private readonly ILogger<EomStore> _log;

    public EomStore(Sql sql, PeopleDirectory people, Notifier notify, ILogger<EomStore> log)
    {
        _sql = sql; _people = people; _notify = notify; _log = log;
    }

    // ------------------------------------------------------------------ okuma

    public async Task<(bool Enabled, EomConfig Config)> SettingsAsync(string tenant, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync("""
            SELECT "Enabled","Cadence","Scope","Process","Visibility","RevealTo","StartDay","OpenHour","NominationDays","FinalistCount",
                   "VotingDays","Stage2Days","ReminderDaysBefore","CooldownPeriods","WeightEmployee","WeightManager","WeightHr","AllowComment"
            FROM engagement_eom_settings WHERE "TenantSlug" = $1
            """, r => (r.GetBoolean(0), new EomConfig(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
                r.GetDecimal(14), r.GetDecimal(15), r.GetDecimal(16), r.GetBoolean(17))), ct, tenant);
        return rows.Count == 0 ? (false, EomConfig.Default) : rows[0];
    }

    private const string PeriodCols = """
        "Id","PeriodKey","Status","Config"::text,"NominationStartsAt","VotingStartsAt","Stage1EndsAt","Stage2EndsAt","Salt",
        "UpcomingSentAt","OpenedSentAt","VotingSentAt","LastCallSentAt","Stage2SentAt","AnnouncedAt","CreatedAt","ClosedAt"
        """;

    private static EomPeriodRow MapPeriod(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<EomConfig>(r.GetString(3), Json)!,
        r.Ts(4), r.GetFieldValue<DateTime>(5), r.GetFieldValue<DateTime>(6), r.Ts(7), r.IsDBNull(8) ? null : r.GetFieldValue<byte[]>(8),
        r.Ts(9), r.Ts(10), r.Ts(11), r.Ts(12), r.Ts(13), r.Ts(14), r.GetFieldValue<DateTime>(15), r.Ts(16));

    public Task<List<EomPeriodRow>> PeriodsAsync(string tenant, CancellationToken ct, int limit = 24)
        => _sql.QueryAsync($"SELECT {PeriodCols} FROM engagement_eom_periods WHERE \"TenantSlug\" = $1 ORDER BY \"VotingStartsAt\" DESC LIMIT $2",
            MapPeriod, ct, tenant, limit);

    public async Task<EomPeriodRow?> PeriodAsync(string tenant, Guid id, CancellationToken ct)
        => (await _sql.QueryAsync($"SELECT {PeriodCols} FROM engagement_eom_periods WHERE \"TenantSlug\" = $1 AND \"Id\" = $2",
            MapPeriod, ct, tenant, id)).FirstOrDefault();

    /// <summary>Sürmekte olan (kapanmamış, iptal edilmemiş) en yeni dönem.</summary>
    public async Task<EomPeriodRow?> ActiveAsync(string tenant, CancellationToken ct)
        => (await _sql.QueryAsync($"""
            SELECT {PeriodCols} FROM engagement_eom_periods
            WHERE "TenantSlug" = $1 AND "Status" NOT IN ('Closed','Cancelled') ORDER BY "VotingStartsAt" LIMIT 1
            """, MapPeriod, ct, tenant)).FirstOrDefault();

    /// <summary>Sonucu açıklanmış en yeni dönem (ana sayfa kartı bir sonraki açıklamaya kadar bunu gösterir).</summary>
    public async Task<EomPeriodRow?> LatestAnnouncedAsync(string tenant, CancellationToken ct)
        => (await _sql.QueryAsync($"""
            SELECT {PeriodCols} FROM engagement_eom_periods
            WHERE "TenantSlug" = $1 AND "Status" = 'Closed' AND "AnnouncedAt" IS NOT NULL ORDER BY "AnnouncedAt" DESC LIMIT 1
            """, MapPeriod, ct, tenant)).FirstOrDefault();

    public Task<List<EomUnitRow>> UnitsAsync(string tenant, Guid periodId, CancellationToken ct)
        => _sql.QueryAsync("""
            SELECT "Id","PeriodId","Stage","DepartmentId","Name","ParentUnitId","HeadEmployeeId","MemberIds","CandidateIds","Status",
                   "WinnerEmployeeId","WinnerScore","TiedIds","ClosedAt"
            FROM engagement_eom_units WHERE "TenantSlug" = $1 AND "PeriodId" = $2 ORDER BY "Stage" DESC, "Name"
            """, r => new EomUnitRow(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetString(4), r.GuidOrNull(5), r.GuidOrNull(6),
                r.GetFieldValue<Guid[]>(7), r.GetFieldValue<Guid[]>(8), r.GetString(9), r.GuidOrNull(10), r.Dec(11),
                r.IsDBNull(12) ? [] : r.GetFieldValue<Guid[]>(12), r.Ts(13)), ct, tenant, periodId);

    public sealed record VoteRow(Guid UnitId, string VoterKey, Guid? VoterEmployeeId, Guid Nominee, decimal Weight, string? Comment);

    public Task<List<VoteRow>> VotesAsync(string tenant, Guid periodId, CancellationToken ct)
        => _sql.QueryAsync("""
            SELECT v."UnitId", v."VoterKey", v."VoterEmployeeId", v."NomineeEmployeeId", v."Weight", v."Comment"
            FROM engagement_eom_votes v JOIN engagement_eom_units u ON u."Id" = v."UnitId"
            WHERE v."TenantSlug" = $1 AND u."PeriodId" = $2
            """, r => new VoteRow(r.GetGuid(0), r.GetString(1), r.GuidOrNull(2), r.GetGuid(3), r.GetDecimal(4), r.Str(5)), ct, tenant, periodId);

    public Task<List<(Guid UnitId, string Key, Guid Nominee)>> NominationsAsync(string tenant, Guid periodId, CancellationToken ct)
        => _sql.QueryAsync("""
            SELECT n."UnitId", n."NominatorKey", n."NomineeEmployeeId"
            FROM engagement_eom_nominations n JOIN engagement_eom_units u ON u."Id" = n."UnitId"
            WHERE n."TenantSlug" = $1 AND u."PeriodId" = $2
            """, r => (r.GetGuid(0), r.GetString(1), r.GetGuid(2)), ct, tenant, periodId);

    // ------------------------------------------------------------------ yaşam döngüsü

    /// <summary>Ayar açık ya da sürmekte dönemi olan kiracılar (işçi bunları dolaşır).</summary>
    public Task<List<string>> TenantsAsync(CancellationToken ct) => _sql.QueryAsync("""
        SELECT "TenantSlug" FROM engagement_eom_settings WHERE "Enabled"
        UNION SELECT DISTINCT "TenantSlug" FROM engagement_eom_periods WHERE "Status" NOT IN ('Closed','Cancelled')
        """, r => r.GetString(0), ct);

    /// <summary>Bir kiracı için zamanı gelen geçişleri yapar; gerekirse sıradaki dönemi kurar.</summary>
    public async Task TickAsync(string tenant, DateTime now, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var (enabled, cfg) = await SettingsAsync(tenant, ct);
            var active = await _sql.QueryAsync($"""
                SELECT {PeriodCols} FROM engagement_eom_periods WHERE "TenantSlug" = $1 AND "Status" NOT IN ('Closed','Cancelled')
                ORDER BY "VotingStartsAt"
                """, MapPeriod, ct, tenant);
            foreach (var p in active) await AdvanceAsync(tenant, p, now, ct);

            if (!enabled) return;
            // Sıradaki dönem, başlamadan (hatırlatma + 1 gün) önce kurulur; ayarlar o anda dondurulur.
            var next = EomLogic.NextSchedule(cfg, now);
            if (now < next.FirstStart.AddDays(-(cfg.ReminderDaysBefore + 1))) return;
            await CreateAsync(tenant, next, cfg, ct);
        }
        finally { Gate.Release(); }
    }

    private async Task<Guid?> CreateAsync(string tenant, EomSchedule s, EomConfig cfg, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var n = await _sql.ExecuteAsync("""
            INSERT INTO engagement_eom_periods ("Id","TenantSlug","PeriodKey","Status","Config","NominationStartsAt","VotingStartsAt","Stage1EndsAt","Stage2EndsAt","CreatedAt")
            VALUES ($1,$2,$3,'Scheduled',$4::jsonb,$5,$6,$7,$8,now())
            ON CONFLICT DO NOTHING
            """, ct, id, tenant, s.PeriodKey, JsonSerializer.Serialize(cfg, Json), s.NominationStartsAt, s.VotingStartsAt, s.Stage1EndsAt, s.Stage2EndsAt);
        if (n == 1) _log.LogInformation("Ayın çalışanı dönemi kuruldu: {Tenant} {Key}", tenant, s.PeriodKey);
        return n == 1 ? id : null;
    }

    /// <summary>İK "şimdi başlat": takvimi beklemeden bugünden başlayan özel dönem (ayarların süreleriyle).</summary>
    public async Task<Guid?> StartNowAsync(string tenant, EomConfig cfg, DateTime now, CancellationToken ct)
    {
        if (await ActiveAsync(tenant, ct) is { Status: not Eom.Scheduled }) return null;
        await _sql.ExecuteAsync("UPDATE engagement_eom_periods SET \"Status\" = 'Cancelled', \"ClosedAt\" = now() WHERE \"TenantSlug\" = $1 AND \"Status\" = 'Scheduled'", ct, tenant);
        DateTime? nomination = cfg.Process == Eom.Nominate ? now : null;
        var voting = cfg.Process == Eom.Nominate ? now.AddDays(cfg.NominationDays) : now;
        var s1 = voting.AddDays(cfg.VotingDays);
        DateTime? s2 = cfg.TwoStage ? s1.AddDays(cfg.Stage2Days) : null;
        var local = now + Eom.LocalOffset;
        var id = await CreateAsync(tenant, new EomSchedule($"M-{local:yyyyMMddHHmmssfff}", nomination, voting, s1, s2), cfg, ct);
        if (id is null) return null;
        // Yaklaşıyor e-postası gereksiz (hemen açılıyor).
        await _sql.ExecuteAsync("UPDATE engagement_eom_periods SET \"UpcomingSentAt\" = now() WHERE \"Id\" = $1", ct, id.Value);
        await TickAsync(tenant, now, ct);
        return id;
    }

    /// <summary>İK: sürmekte olan aşamayı şimdi bitirir (sonraki aşamaya geçer ya da sonuçlanır).</summary>
    public async Task<bool> SkipPhaseAsync(string tenant, Guid id, DateTime now, CancellationToken ct)
    {
        var p = await PeriodAsync(tenant, id, ct);
        if (p is null) return false;
        var col = p.Status switch
        {
            Eom.Scheduled => p.NominationStartsAt is null ? "VotingStartsAt" : "NominationStartsAt",
            Eom.Nominating => "VotingStartsAt",
            Eom.Voting => "Stage1EndsAt",
            Eom.Stage2 => "Stage2EndsAt",
            _ => null,
        };
        if (col is null) return false;
        await _sql.ExecuteAsync($"UPDATE engagement_eom_periods SET \"{col}\" = $3 WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, tenant, id, now);
        if (p.Status == Eom.Scheduled) await _sql.ExecuteAsync("UPDATE engagement_eom_periods SET \"UpcomingSentAt\" = coalesce(\"UpcomingSentAt\", now()) WHERE \"Id\" = $1", ct, id);
        await TickAsync(tenant, now, ct);
        return true;
    }

    public async Task<bool> CancelAsync(string tenant, Guid id, CancellationToken ct)
        => await _sql.ExecuteAsync("""
            UPDATE engagement_eom_periods SET "Status" = 'Cancelled', "Salt" = NULL, "ClosedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" NOT IN ('Closed','Cancelled')
            """, ct, tenant, id) == 1;

    /// <summary>İK: beraberliği seçilen kişi lehine çözer; başka beraberlik kalmadıysa dönem sonuçlanır.</summary>
    public async Task<string?> ResolveTieAsync(string tenant, Guid unitId, Guid winner, DateTime now, CancellationToken ct)
    {
        var n = await _sql.ExecuteAsync("""
            UPDATE engagement_eom_units SET "WinnerEmployeeId" = $3, "Status" = 'Closed', "ClosedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" = 'Tie' AND $3 = ANY("TiedIds")
            """, ct, tenant, unitId, winner);
        if (n == 0) return "Bu birimde çözülecek beraberlik yok ya da kişi berabere kalanlardan değil";
        await TickAsync(tenant, now, ct);
        return null;
    }

    private async Task AdvanceAsync(string tenant, EomPeriodRow p, DateTime now, CancellationToken ct)
    {
        switch (p.Status)
        {
            case Eom.Scheduled:
                if (now >= p.FirstStart) await OpenAsync(tenant, p, now, ct);
                else if (p.UpcomingSentAt is null && p.Config.ReminderDaysBefore > 0 && now >= p.FirstStart.AddDays(-p.Config.ReminderDaysBefore)
                         && await Claim(p.Id, "UpcomingSentAt", ct))
                    await MailUpcomingAsync(tenant, p, ct);
                break;
            case Eom.Nominating:
                if (now >= p.VotingStartsAt) await StartVotingAsync(tenant, p, ct);
                break;
            case Eom.Voting:
                if (now >= p.Stage1EndsAt) await CloseStage1Async(tenant, p, ct);
                else if (p.LastCallSentAt is null && now >= EomLogic.LastCallAt(p.VotingStartsAt, p.Stage1EndsAt) && await Claim(p.Id, "LastCallSentAt", ct))
                    await MailLastCallAsync(tenant, p, ct);
                break;
            case Eom.Stage2:
                if (now >= (p.Stage2EndsAt ?? p.Stage1EndsAt) && await SetStatus(p.Id, Eom.Stage2, Eom.Tie, ct))
                {
                    await CloseUnitsAsync(tenant, p, 2, ct);
                    await FinalizeAsync(tenant, p with { Status = Eom.Tie }, ct);
                }
                break;
            case Eom.Tie:
                await FinalizeAsync(tenant, p, ct);
                break;
        }
    }

    /// <summary>Bildirim işaretini koşullu olarak "sahiplenir" (aynı e-posta iki kez gitmez).</summary>
    private async Task<bool> Claim(Guid periodId, string column, CancellationToken ct)
        => await _sql.ExecuteAsync($"UPDATE engagement_eom_periods SET \"{column}\" = now() WHERE \"Id\" = $1 AND \"{column}\" IS NULL", ct, periodId) == 1;

    private async Task<bool> SetStatus(Guid periodId, string from, string to, CancellationToken ct)
        => await _sql.ExecuteAsync("UPDATE engagement_eom_periods SET \"Status\" = $3 WHERE \"Id\" = $1 AND \"Status\" = $2", ct, periodId, from, to) == 1;

    /// <summary>Açılış: birimler ve üyeler sabitlenir, gizli kipte dönem tuzu üretilir.</summary>
    private async Task OpenAsync(string tenant, EomPeriodRow p, DateTime now, CancellationToken ct)
    {
        var cfg = p.Config;
        var next = cfg.Process == Eom.Nominate && now < p.VotingStartsAt ? Eom.Nominating : Eom.Voting;
        if (!await SetStatus(p.Id, Eom.Scheduled, next, ct)) return;
        if (!cfg.StoresIdentity)
            await _sql.ExecuteAsync("UPDATE engagement_eom_periods SET \"Salt\" = $2 WHERE \"Id\" = $1", ct, p.Id, RandomNumberGenerator.GetBytes(32));

        var depts = await _sql.QueryAsync("""
            SELECT "Id","ParentDepartmentId","Name","HeadEmployeeId" FROM organization_departments WHERE "TenantSlug" = $1
            """, r => new EomDept(r.GetGuid(0), r.GuidOrNull(1), r.GetString(2), r.GuidOrNull(3)), ct, tenant);
        var people = await _people.ListAsync(tenant, ct);
        var members = people.Where(x => x.DepartmentId is not null).Select(x => new EomMember(x.Id, x.DepartmentId!.Value)).ToList();
        var plans = EomLogic.BuildUnits(cfg.Scope, depts, members);
        var cooldown = (await CooldownAsync(tenant, cfg.CooldownPeriods, ct)).ToHashSet();

        var ids = plans.ToDictionary(u => u.Key, _ => Guid.NewGuid());
        foreach (var u in plans.OrderByDescending(u => u.Stage))
        {
            var eligible = u.Members.Where(m => !cooldown.Contains(m)).ToArray();
            string status; Guid? winner = null;
            if (u.Stage == 2) status = Eom.UnitPending;
            else if (u.Members.Count == 1) { status = eligible.Length == 1 ? Eom.UnitClosed : Eom.UnitNoVotes; winner = eligible.Length == 1 ? eligible[0] : null; }
            else if (eligible.Length == 0) status = Eom.UnitNoVotes;
            else status = Eom.UnitOpen;
            await _sql.ExecuteAsync("""
                INSERT INTO engagement_eom_units ("Id","TenantSlug","PeriodId","Stage","DepartmentId","Name","ParentUnitId","HeadEmployeeId","MemberIds","CandidateIds","Status","WinnerEmployeeId","ClosedAt")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12, CASE WHEN $11 IN ('Closed','NoVotes') THEN now() END)
                """, ct, ids[u.Key], tenant, p.Id, u.Stage, u.DepartmentId, u.Name, u.ParentKey is null ? null : ids[u.ParentKey], u.HeadId,
                u.Members.ToArray(), u.Stage == 2 ? Array.Empty<Guid>() : eligible, status, winner);
        }
        _log.LogInformation("Ayın çalışanı dönemi açıldı: {Tenant} {Key}, {Units} birim", tenant, p.Key, plans.Count);

        if (next == Eom.Nominating) { if (await Claim(p.Id, "OpenedSentAt", ct)) await MailOpenAsync(tenant, p, nominating: true, ct); }
        else if (await Claim(p.Id, "VotingSentAt", ct)) await MailOpenAsync(tenant, p, nominating: false, ct);
    }

    /// <summary>Aday gösterme biter: finalistler belirlenir, oylama açılır.</summary>
    private async Task StartVotingAsync(string tenant, EomPeriodRow p, CancellationToken ct)
    {
        if (!await SetStatus(p.Id, Eom.Nominating, Eom.Voting, ct)) return;
        var nominations = await NominationsAsync(tenant, p.Id, ct);
        foreach (var u in (await UnitsAsync(tenant, p.Id, ct)).Where(u => u.Stage == 1 && u.Status == Eom.UnitOpen))
        {
            var finalists = EomLogic.Finalists(nominations.Where(n => n.UnitId == u.Id).Select(n => n.Nominee), u.Candidates, p.Config.FinalistCount);
            await _sql.ExecuteAsync("UPDATE engagement_eom_units SET \"CandidateIds\" = $2 WHERE \"Id\" = $1", ct, u.Id, finalists.ToArray());
        }
        if (await Claim(p.Id, "VotingSentAt", ct)) await MailOpenAsync(tenant, p, nominating: false, ct);
    }

    /// <summary>Bir aşamanın açık birimlerini sayar (tek aday varsa oy beklemeden kazanır).</summary>
    private async Task CloseUnitsAsync(string tenant, EomPeriodRow p, int stage, CancellationToken ct)
    {
        var votes = await VotesAsync(tenant, p.Id, ct);
        foreach (var u in (await UnitsAsync(tenant, p.Id, ct)).Where(u => u.Stage == stage && u.Status == Eom.UnitOpen))
        {
            var t = EomLogic.Tally(votes.Where(v => v.UnitId == u.Id && u.Candidates.Contains(v.Nominee)).Select(v => new EomBallot(v.Nominee, v.Weight)));
            var (status, winner, score, tied) = u.Candidates.Length == 1 && t.Ranking.Count == 0
                ? (Eom.UnitClosed, (Guid?)u.Candidates[0], (decimal?)0m, Array.Empty<Guid>())
                : t.Winner is { } w ? (Eom.UnitClosed, (Guid?)w, (decimal?)t.Ranking[0].Score, Array.Empty<Guid>())
                : t.Tied.Count > 0 ? (Eom.UnitTie, null, (decimal?)t.Ranking[0].Score, t.Tied.ToArray())
                : (Eom.UnitNoVotes, null, null, Array.Empty<Guid>());
            await _sql.ExecuteAsync("""
                UPDATE engagement_eom_units SET "Status" = $2, "WinnerEmployeeId" = $3, "WinnerScore" = $4, "TiedIds" = $5, "ClosedAt" = now()
                WHERE "Id" = $1 AND "Status" = 'Open'
                """, ct, u.Id, status, winner, score, tied);
        }
    }

    /// <remarks>
    /// Sayım öncesi dönem "Tie" durumuna alınır (geçişi sahiplenmek için): beraberlik kalmadıysa
    /// <see cref="FinalizeAsync"/> hemen Closed yapar, kaldıysa dönem İK çözene kadar Tie'da bekler.
    /// </remarks>
    private async Task CloseStage1Async(string tenant, EomPeriodRow p, CancellationToken ct)
    {
        if (!p.Config.TwoStage)
        {
            if (!await SetStatus(p.Id, Eom.Voting, Eom.Tie, ct)) return;
            await CloseUnitsAsync(tenant, p, 1, ct);
            await FinalizeAsync(tenant, p with { Status = Eom.Tie }, ct);
            return;
        }
        if (!await SetStatus(p.Id, Eom.Voting, Eom.Stage2, ct)) return;
        await CloseUnitsAsync(tenant, p, 1, ct);
        // 1. aşamada berabere kalanların hepsi finale kalır (2. aşama bekletilmez).
        var units = await UnitsAsync(tenant, p.Id, ct);
        foreach (var root in units.Where(u => u.Stage == 2))
        {
            var finalists = units.Where(c => c.ParentUnitId == root.Id)
                .SelectMany(c => c.Winner is { } w ? [w] : c.Tied).Distinct().ToArray();
            var (status, winner) = finalists.Length switch { 0 => (Eom.UnitNoVotes, (Guid?)null), 1 => (Eom.UnitClosed, (Guid?)finalists[0]), _ => (Eom.UnitOpen, null) };
            await _sql.ExecuteAsync("""
                UPDATE engagement_eom_units SET "CandidateIds" = $2, "Status" = $3, "WinnerEmployeeId" = $4,
                       "ClosedAt" = CASE WHEN $3 = 'Open' THEN NULL ELSE now() END
                WHERE "Id" = $1
                """, ct, root.Id, finalists, status, winner);
        }
        if (await Claim(p.Id, "Stage2SentAt", ct)) await MailStage2Async(tenant, p, ct);
        // 2. aşamada oylanacak birim kalmadıysa beklemeden sonuçlanır.
        if (!(await UnitsAsync(tenant, p.Id, ct)).Any(u => u.Stage == 2 && u.Status == Eom.UnitOpen))
        {
            await SetStatus(p.Id, Eom.Stage2, Eom.Tie, ct);
            await FinalizeAsync(tenant, p with { Status = Eom.Tie }, ct);
        }
    }

    /// <summary>Sonuç birimlerinde beraberlik kalmadıysa dönemi kapatır, tuzu siler ve sonucu duyurur.</summary>
    private async Task FinalizeAsync(string tenant, EomPeriodRow p, CancellationToken ct)
    {
        var units = await UnitsAsync(tenant, p.Id, ct);
        if (FinalUnits(units, p.Config).Any(u => u.Status is Eom.UnitTie or Eom.UnitOpen or Eom.UnitPending)) return;
        var n = await _sql.ExecuteAsync("""
            UPDATE engagement_eom_periods SET "Status" = 'Closed', "Salt" = NULL, "AnnouncedAt" = now(), "ClosedAt" = now()
            WHERE "Id" = $1 AND "Status" = 'Tie'
            """, ct, p.Id);
        if (n == 1) await MailResultsAsync(tenant, p, units, ct);
    }

    /// <summary>Sonucu belirleyen birimler: iki aşamalıda 2. aşama birimleri ve tek aşamalı kalan ana departmanlar.</summary>
    public static IEnumerable<EomUnitRow> FinalUnits(IReadOnlyList<EomUnitRow> units, EomConfig cfg)
        => cfg.TwoStage ? units.Where(u => u.Stage == 2 || u.ParentUnitId is null) : units;

    /// <summary>Son N kapanmış dönemin kazananları (bekleme süresi).</summary>
    public async Task<List<Guid>> CooldownAsync(string tenant, int periods, CancellationToken ct)
        => periods <= 0 ? [] : await _sql.QueryAsync("""
            SELECT DISTINCT u."WinnerEmployeeId" FROM engagement_eom_units u
            WHERE u."TenantSlug" = $1 AND u."WinnerEmployeeId" IS NOT NULL AND u."PeriodId" IN (
                SELECT "Id" FROM engagement_eom_periods WHERE "TenantSlug" = $1 AND "Status" = 'Closed' ORDER BY "AnnouncedAt" DESC LIMIT $2)
            """, r => r.GetGuid(0), ct, tenant, periods);

    // ------------------------------------------------------------------ oy ve aday gösterme

    /// <summary>Kişi yönetici mi: yönetici rolü, bir departmanın başı ya da etkin bir ekibin lideri.</summary>
    public async Task<bool> IsStructuralManagerAsync(string tenant, Guid employeeId, CancellationToken ct)
        => await _sql.ScalarAsync("""
            SELECT EXISTS (SELECT 1 FROM organization_departments WHERE "TenantSlug" = $1 AND "HeadEmployeeId" = $2)
                OR EXISTS (SELECT 1 FROM organization_teams WHERE "TenantSlug" = $1 AND "LeadEmployeeId" = $2 AND "IsActive")
            """, ct, tenant, employeeId) is true;

    public async Task<string?> VoteAsync(string tenant, Guid unitId, Guid voter, Guid nominee, string? comment, decimal weight, CancellationToken ct)
    {
        var (p, u, err) = await ActionContextAsync(tenant, unitId, voter, ct);
        if (err is not null) return err;
        var phase = u!.Stage == 2 ? Eom.Stage2 : Eom.Voting;
        if (p!.Status != phase || u.Status != Eom.UnitOpen) return "Bu birimde şu an oylama açık değil";
        if (nominee == voter) return "Kendinize oy veremezsiniz";
        if (!u.Candidates.Contains(nominee)) return "Seçtiğiniz kişi bu oylamada aday değil";
        comment = string.IsNullOrWhiteSpace(comment) || !p.Config.AllowComment ? null : comment.Trim();
        if (comment is { Length: > 280 }) return "Gerekçe en fazla 280 karakter olabilir";
        var key = EomLogic.VoterKey(p.Config, p.Salt, u.Id, voter);
        await _sql.ExecuteAsync("""
            INSERT INTO engagement_eom_votes ("Id","TenantSlug","UnitId","VoterKey","VoterEmployeeId","NomineeEmployeeId","Weight","Comment","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,now(),now())
            ON CONFLICT ("UnitId","VoterKey") DO UPDATE SET "NomineeEmployeeId" = EXCLUDED."NomineeEmployeeId", "Weight" = EXCLUDED."Weight",
                "Comment" = EXCLUDED."Comment", "UpdatedAt" = now()
            """, ct, Guid.NewGuid(), tenant, u.Id, key, p.Config.StoresIdentity ? voter : null, nominee, weight, comment);
        return null;
    }

    public async Task<string?> NominateAsync(string tenant, Guid unitId, Guid nominator, Guid nominee, CancellationToken ct)
    {
        var (p, u, err) = await ActionContextAsync(tenant, unitId, nominator, ct);
        if (err is not null) return err;
        if (p!.Status != Eom.Nominating || u!.Stage != 1 || u.Status != Eom.UnitOpen) return "Bu birimde şu an aday gösterme açık değil";
        if (nominee == nominator) return "Kendinizi aday gösteremezsiniz";
        if (!u.Candidates.Contains(nominee)) return "Bu kişi aday gösterilemez";
        var key = EomLogic.VoterKey(p.Config, p.Salt, u.Id, nominator);
        await _sql.ExecuteAsync("""
            INSERT INTO engagement_eom_nominations ("Id","TenantSlug","UnitId","NominatorKey","NominatorEmployeeId","NomineeEmployeeId","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,now())
            ON CONFLICT ("UnitId","NominatorKey") DO UPDATE SET "NomineeEmployeeId" = EXCLUDED."NomineeEmployeeId", "CreatedAt" = now()
            """, ct, Guid.NewGuid(), tenant, u.Id, key, p.Config.StoresIdentity ? nominator : null, nominee);
        return null;
    }

    private async Task<(EomPeriodRow?, EomUnitRow?, string?)> ActionContextAsync(string tenant, Guid unitId, Guid employee, CancellationToken ct)
    {
        var pid = await _sql.ScalarAsync("SELECT \"PeriodId\" FROM engagement_eom_units WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, tenant, unitId);
        if (pid is not Guid periodId) return (null, null, "Oylama birimi bulunamadı");
        var p = await PeriodAsync(tenant, periodId, ct);
        var u = (await UnitsAsync(tenant, periodId, ct)).First(x => x.Id == unitId);
        if (p is null) return (null, null, "Dönem bulunamadı");
        if (!u.Members.Contains(employee)) return (p, u, "Bu oylamada oy kullanma hakkınız yok");
        return (p, u, null);
    }

    /// <summary>Kişinin bu birimdeki seçimi (oy ya da aday gösterme); yoksa null.</summary>
    public async Task<(Guid? Nominee, string? Comment)> MyChoiceAsync(string tenant, EomPeriodRow p, EomUnitRow u, Guid employee, bool nomination, CancellationToken ct)
    {
        if (!p.Config.StoresIdentity && p.Salt is null) return (null, null); // kapanmış gizli dönem: eşleştirme yok
        var key = EomLogic.VoterKey(p.Config, p.Salt, u.Id, employee);
        if (nomination)
            return ((Guid?)await _sql.ScalarAsync("SELECT \"NomineeEmployeeId\" FROM engagement_eom_nominations WHERE \"UnitId\" = $1 AND \"NominatorKey\" = $2", ct, u.Id, key), null);
        var rows = await _sql.QueryAsync("SELECT \"NomineeEmployeeId\", \"Comment\" FROM engagement_eom_votes WHERE \"UnitId\" = $1 AND \"VoterKey\" = $2",
            r => ((Guid?)r.GetGuid(0), r.Str(1)), ct, u.Id, key);
        return rows.Count == 0 ? (null, null) : rows[0];
    }

    /// <summary>Henüz oy vermemiş üyeler (son çağrı e-postası için; gizli kipte dönem tuzuyla eşlenir).</summary>
    private async Task<List<Guid>> NonVotersAsync(string tenant, EomPeriodRow p, EomUnitRow u, CancellationToken ct)
    {
        var keys = (await _sql.QueryAsync("SELECT \"VoterKey\" FROM engagement_eom_votes WHERE \"UnitId\" = $1", r => r.GetString(0), ct, u.Id)).ToHashSet();
        return u.Members.Where(m => !keys.Contains(EomLogic.VoterKey(p.Config, p.Salt, u.Id, m))).ToList();
    }

    // ------------------------------------------------------------------ bildirimler

    private static string Link => (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost").TrimEnd('/') + "/panel/ayin-calisani";
    private static string When(DateTime utc) => (utc + Eom.LocalOffset).ToString("dd.MM.yyyy HH:mm");

    private static (string Tr, string En) VisibilityNotice(EomConfig c) => c.Visibility switch
    {
        Eom.HrSeesVotes => ("Bu oylamada İK, kimin kime oy verdiğini görebilir.", "In this vote, HR can see who voted for whom."),
        Eom.RevealAfter when c.RevealTo == Eom.RevealDepartment => ("Oylama sürerken oylar gizlidir; sonuç açıklanınca kimin kime oy verdiği birimdeki herkese açılır.",
            "Votes are secret while voting is open; once the result is announced, who voted for whom is shown to everyone in the unit."),
        Eom.RevealAfter => ("Oylama sürerken oylar gizlidir; sonuç açıklanınca kimin kime oy verdiğini İK ve departman yöneticisi görebilir.",
            "Votes are secret while voting is open; once the result is announced, HR and the department head can see who voted for whom."),
        _ => ("Oylar gizlidir: kimin kime oy verdiğini kimse göremez.", "Votes are secret: no one can see who voted for whom."),
    };

    private async Task SendAsync(string tenant, IEnumerable<Guid> recipients, string code, string subjectTr, string subjectEn, string bodyTr, string bodyEn, CancellationToken ct)
    {
        foreach (var id in recipients.Distinct())
        {
            await _notify.LocalizedAsync(tenant, id, subjectTr, subjectEn, bodyTr, bodyEn, code, ct, "InApp", Link, "Ayın çalışanı", "Employee of the month");
            await _notify.LocalizedAsync(tenant, id, subjectTr, subjectEn, bodyTr, bodyEn, code, ct, "Email", Link, "Oylamaya git", "Go to the vote");
        }
    }

    private async Task MailUpcomingAsync(string tenant, EomPeriodRow p, CancellationToken ct)
    {
        var people = (await _people.ListAsync(tenant, ct)).Where(x => x.DepartmentId is not null).Select(x => x.Id);
        var what = p.NominationStartsAt is null ? ("oylaması", "voting") : ("aday gösterme dönemi", "nominations");
        await SendAsync(tenant, people, "eom.upcoming",
            "Ayın çalışanı oylaması yaklaşıyor", "Employee of the month vote is coming up",
            $"Ayın çalışanı {what.Item1} {When(p.FirstStart)} tarihinde başlıyor. Departmanınızda en çok takdiri hak eden çalışma arkadaşınızı seçebileceksiniz.",
            $"Employee of the month {what.Item2} start on {When(p.FirstStart)}. You will be able to choose the colleague in your department who deserves it most.", ct);
    }

    private async Task MailOpenAsync(string tenant, EomPeriodRow p, bool nominating, CancellationToken ct)
    {
        var units = (await UnitsAsync(tenant, p.Id, ct)).Where(u => u.Stage == 1 && u.Status == Eom.UnitOpen && u.Candidates.Length > 1);
        var notice = VisibilityNotice(p.Config);
        if (nominating)
            await SendAsync(tenant, units.SelectMany(u => u.Members), "eom.nominate",
                "Ayın çalışanı: aday gösterme başladı", "Employee of the month: nominations are open",
                $"Ayın çalışanı için aday gösterme başladı ({When(p.VotingStartsAt)} tarihine kadar). En çok aday gösterilen kişiler oylamaya katılır. {notice.Tr}",
                $"Nominations for employee of the month are open until {When(p.VotingStartsAt)}. The most nominated colleagues go on to the vote. {notice.En}", ct);
        else
            await SendAsync(tenant, units.SelectMany(u => u.Members), "eom.vote",
                "Ayın çalışanı: oylama başladı", "Employee of the month: voting is open",
                $"Ayın çalışanı oylaması başladı, {When(p.Stage1EndsAt)} tarihinde kapanıyor. {notice.Tr}",
                $"Employee of the month voting is open and closes on {When(p.Stage1EndsAt)}. {notice.En}", ct);
    }

    private async Task MailLastCallAsync(string tenant, EomPeriodRow p, CancellationToken ct)
    {
        var fresh = await PeriodAsync(tenant, p.Id, ct) ?? p;
        var pending = new List<Guid>();
        foreach (var u in (await UnitsAsync(tenant, p.Id, ct)).Where(u => u.Stage == 1 && u.Status == Eom.UnitOpen && u.Candidates.Length > 1))
            pending.AddRange(await NonVotersAsync(tenant, fresh, u, ct));
        await SendAsync(tenant, pending, "eom.lastcall",
            "Ayın çalışanı oylaması kapanmak üzere", "Employee of the month voting is closing soon",
            $"Henüz oy kullanmadınız. Oylama {When(p.Stage1EndsAt)} tarihinde kapanıyor.",
            $"You haven't voted yet. Voting closes on {When(p.Stage1EndsAt)}.", ct);
    }

    private async Task MailStage2Async(string tenant, EomPeriodRow p, CancellationToken ct)
    {
        var units = (await UnitsAsync(tenant, p.Id, ct)).Where(u => u.Stage == 2 && u.Status == Eom.UnitOpen).ToList();
        var end = When(p.Stage2EndsAt ?? p.Stage1EndsAt);
        await SendAsync(tenant, units.SelectMany(u => u.Members), "eom.final",
            "Ayın çalışanı: final oylaması başladı", "Employee of the month: the final vote is open",
            $"Alt departman kazananları belli oldu. Şimdi ana departmanınızın ayın çalışanını finalistler arasından seçin ({end} tarihine kadar).",
            $"The sub-department winners are in. Now choose your main department's employee of the month among the finalists (until {end}).", ct);
        await SendAsync(tenant, units.SelectMany(u => u.Candidates), "eom.finalist",
            "Tebrikler, finale kaldınız", "Congratulations, you are a finalist",
            "Alt departmanınızın oylamasını kazandınız ve ana departman finaline kaldınız.",
            "You won your sub-department's vote and are a finalist for your main department.", ct);
    }

    private async Task MailResultsAsync(string tenant, EomPeriodRow p, List<EomUnitRow> units, CancellationToken ct)
    {
        var people = (await _people.ListAsync(tenant, ct, includeTerminated: true)).ToDictionary(x => x.Id);
        foreach (var u in FinalUnits(units, p.Config).Where(u => u.Winner is not null))
        {
            var name = people.GetValueOrDefault(u.Winner!.Value)?.Name ?? "—";
            await SendAsync(tenant, [u.Winner.Value], "eom.winner",
                "Tebrikler, ayın çalışanı seçildiniz!", "Congratulations, you are employee of the month!",
                $"{u.Name} ayın çalışanı seçildiniz. Fotoğrafınız bir sonraki oylamaya kadar ana sayfada yer alacak.",
                $"You have been chosen employee of the month for {u.Name}. Your photo will be on the home page until the next vote.", ct);
            await SendAsync(tenant, u.Members.Where(m => m != u.Winner), "eom.result",
                $"Ayın çalışanı: {name}", $"Employee of the month: {name}",
                $"{u.Name} ayın çalışanı {name} oldu. Tebrikler!",
                $"{name} is the employee of the month for {u.Name}. Congratulations!", ct);
        }
    }
}
