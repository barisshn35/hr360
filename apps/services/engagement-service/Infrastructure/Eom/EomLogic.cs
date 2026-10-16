using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace EngagementService.Infrastructure.Eom;

/// <summary>"Ayın çalışanı" ayarlarının değerleri (veritabanı ve API'de metin olarak).</summary>
public static class Eom
{
    public const string Monthly = "Monthly", Quarterly = "Quarterly";
    /// <summary>Oylama birimi: ana departman (alt departmanlarıyla) | her departman ayrı | iki aşamalı.</summary>
    public const string TopLevel = "TopLevel", EachDepartment = "EachDepartment", TwoStage = "TwoStage";
    public const string Direct = "Direct", Nominate = "Nominate";
    /// <summary>
    /// Görünürlük: WinnerOnly (gizli oy; çalışan yalnız kazananı, İK puan sıralamasını görür) |
    /// RankingPublic (gizli oy; kapanışta sıralama birim üyelerine açık) | HrSeesVotes (İK kimin kime oy
    /// verdiğini görür) | RevealAfter (oylama sürerken gizli; sonuç açıklanınca RevealTo'ya açılır).
    /// </summary>
    public const string WinnerOnly = "WinnerOnly", RankingPublic = "RankingPublic", HrSeesVotes = "HrSeesVotes", RevealAfter = "RevealAfter";
    public const string RevealDepartment = "Department", RevealHrAndHead = "HrAndHead";

    public static readonly string[] Cadences = [Monthly, Quarterly];
    public static readonly string[] Scopes = [TopLevel, EachDepartment, TwoStage];
    public static readonly string[] Processes = [Direct, Nominate];
    public static readonly string[] Visibilities = [WinnerOnly, RankingPublic, HrSeesVotes, RevealAfter];
    public static readonly string[] RevealTargets = [RevealDepartment, RevealHrAndHead];

    // Dönem durumu
    public const string Scheduled = "Scheduled", Nominating = "Nominating", Voting = "Voting", Stage2 = "Stage2",
        Tie = "Tie", Closed = "Closed", Cancelled = "Cancelled";
    // Birim durumu
    public const string UnitPending = "Pending", UnitOpen = "Open", UnitClosed = "Closed", UnitTie = "Tie", UnitNoVotes = "NoVotes";

    /// <summary>
    /// Türkiye saati (UTC+3, 2016'dan beri yaz saati yok). Kapsayıcı imajında saat dilimi veritabanı
    /// olmayabileceği için sabit fark kullanılır.
    /// </summary>
    public static readonly TimeSpan LocalOffset = TimeSpan.FromHours(3);
}

/// <summary>Bir dönemin açıldığı andaki (dondurulmuş) ayarları.</summary>
public sealed record EomConfig(
    string Cadence, string Scope, string Process, string Visibility, string RevealTo,
    int StartDay, int OpenHour, int NominationDays, int FinalistCount, int VotingDays, int Stage2Days,
    int ReminderDaysBefore, int CooldownPeriods,
    decimal WeightEmployee, decimal WeightManager, decimal WeightHr, bool AllowComment)
{
    public static EomConfig Default => new(Eom.Monthly, Eom.TopLevel, Eom.Direct, Eom.WinnerOnly, Eom.RevealHrAndHead,
        1, 9, 2, 5, 3, 1, 2, 3, 1m, 2m, 2m, true);

    /// <summary>Oy satırında kişi kimliği tutulur mu (yalnızca kimliğin bir gün görüneceği kiplerde).</summary>
    [JsonIgnore] public bool StoresIdentity => Visibility is Eom.HrSeesVotes or Eom.RevealAfter;
    [JsonIgnore] public bool TwoStage => Scope == Eom.TwoStage;

    /// <summary>Ayar doğrulaması; hata yoksa null.</summary>
    public string? Validate()
    {
        if (!Eom.Cadences.Contains(Cadence)) return "Geçersiz sıklık";
        if (!Eom.Scopes.Contains(Scope)) return "Geçersiz oylama düzeyi";
        if (!Eom.Processes.Contains(Process)) return "Geçersiz süreç";
        if (!Eom.Visibilities.Contains(Visibility)) return "Geçersiz görünürlük";
        if (!Eom.RevealTargets.Contains(RevealTo)) return "Geçersiz görünürlük hedefi";
        if (StartDay is < 1 or > 28) return "Başlangıç günü 1-28 arasında olmalı";
        if (OpenHour is < 0 or > 23) return "Açılış saati 0-23 arasında olmalı";
        if (NominationDays is < 1 or > 14) return "Aday gösterme süresi 1-14 gün olmalı";
        if (FinalistCount is < 2 or > 20) return "Finalist sayısı 2-20 olmalı";
        if (VotingDays is < 1 or > 14) return "Oylama süresi 1-14 gün olmalı";
        if (Stage2Days is < 1 or > 14) return "İkinci aşama süresi 1-14 gün olmalı";
        if (ReminderDaysBefore is < 0 or > 14) return "Hatırlatma 0-14 gün önce olmalı";
        if (CooldownPeriods is < 0 or > 24) return "Bekleme süresi 0-24 dönem olmalı";
        foreach (var w in new[] { WeightEmployee, WeightManager, WeightHr })
            if (w is < 0.1m or > 10m) return "Katsayılar 0,1 ile 10 arasında olmalı";
        // Aday gösterme + oylama + 2. aşama bir sonraki dönemin açılışına taşmamalı.
        var span = (Process == Eom.Nominate ? NominationDays : 0) + VotingDays + (Scope == Eom.TwoStage ? Stage2Days : 0);
        if (span > 25) return "Aday gösterme, oylama ve ikinci aşama toplamı 25 günü geçemez";
        return null;
    }
}

/// <summary>Bir dönemin takvimi (UTC).</summary>
public sealed record EomSchedule(string PeriodKey, DateTime? NominationStartsAt, DateTime VotingStartsAt, DateTime Stage1EndsAt, DateTime? Stage2EndsAt)
{
    public DateTime FirstStart => NominationStartsAt ?? VotingStartsAt;
    public DateTime FinalEnd => Stage2EndsAt ?? Stage1EndsAt;
}

public sealed record EomDept(Guid Id, Guid? ParentId, string Name, Guid? HeadId);
public sealed record EomMember(Guid EmployeeId, Guid DepartmentId);

/// <summary>
/// Kurulacak oylama birimi. <see cref="ParentKey"/>: iki aşamalı kipte 1. aşama biriminin bağlı olduğu
/// 2. aşama birimi (yoksa birim kendi başına sonuç birimidir).
/// </summary>
public sealed record EomUnitPlan(string Key, int Stage, Guid DepartmentId, string Name, Guid? HeadId, List<Guid> Members, string? ParentKey);

public sealed record EomBallot(Guid Nominee, decimal Weight);
public sealed record EomStanding(Guid EmployeeId, decimal Score, int Votes);
public sealed record EomTally(List<EomStanding> Ranking, Guid? Winner, List<Guid> Tied);

public static class EomLogic
{
    /// <summary>
    /// Oylamanın hangi döneme ait olduğu: aylıkta açılış günü ayın ilk yarısındaysa önceki ay (1 Kasım'da
    /// "Ekim'in çalışanı"), değilse içinde bulunulan ay; üç aylıkta açılış çeyreğin ilk ayında yapılır ve
    /// önceki çeyrek seçilir. Anahtar: "2026-10" ya da "2026-Q3".
    /// </summary>
    public static string PeriodKey(EomConfig c, int year, int month)
    {
        if (c.Cadence == Eom.Quarterly)
        {
            var q = (month - 1) / 3; // açılış çeyreği (0..3); seçilen önceki çeyrek
            return q == 0 ? $"{year - 1}-Q4" : $"{year}-Q{q}";
        }
        if (c.StartDay > 15) return $"{year:D4}-{month:D2}";
        return month == 1 ? $"{year - 1:D4}-12" : $"{year:D4}-{month - 1:D2}";
    }

    /// <summary>Açılış ayı verilen dönemin takvimi.</summary>
    public static EomSchedule ScheduleFor(EomConfig c, int year, int month)
    {
        var local = new DateTime(year, month, Math.Clamp(c.StartDay, 1, 28), Math.Clamp(c.OpenHour, 0, 23), 0, 0, DateTimeKind.Unspecified);
        var voting = DateTime.SpecifyKind(local - Eom.LocalOffset, DateTimeKind.Utc);
        DateTime? nomination = c.Process == Eom.Nominate ? voting.AddDays(-c.NominationDays) : null;
        var stage1End = voting.AddDays(c.VotingDays);
        DateTime? stage2End = c.TwoStage ? stage1End.AddDays(c.Stage2Days) : null;
        return new EomSchedule(PeriodKey(c, year, month), nomination, voting, stage1End, stage2End);
    }

    /// <summary>
    /// Henüz başlamamış en yakın dönem (yeni dönem yalnızca başlamadan önce kurulur; ayar ay ortasında
    /// açılırsa o ayın kaçmış oylaması kısaltılmış olarak başlatılmaz, sonraki dönem beklenir).
    /// </summary>
    public static EomSchedule NextSchedule(EomConfig c, DateTime nowUtc)
    {
        var d = new DateTime(nowUtc.Year, nowUtc.Month, 1);
        for (var i = 0; i < 15; i++, d = d.AddMonths(1))
        {
            if (c.Cadence == Eom.Quarterly && (d.Month - 1) % 3 != 0) continue;
            var s = ScheduleFor(c, d.Year, d.Month);
            if (s.FirstStart > nowUtc) return s;
        }
        throw new InvalidOperationException("Takvim hesaplanamadı");
    }

    /// <summary>
    /// Oylama birimlerini kurar. Üyelik açılış anındaki güncel departman atamasıdır; departmanı olmayan
    /// çalışan oylamaya katılmaz.
    ///  * TopLevel: her ana departman (üst departmanı olmayan) alt departmanlarıyla tek birim.
    ///  * EachDepartment: her departman yalnız kendine doğrudan atanmış kişilerle.
    ///  * TwoStage: 1. aşamada ana departmanın her alt departmanı (kendi alt ağacıyla) ve ana departmana
    ///    doğrudan bağlı kişiler ayrı birim; 2. aşamada ana departmanın tamamı bu birimlerin kazananları
    ///    arasından seçer. Alt departmanı olmayan ana departman tek aşamalı birim olur.
    /// İki kişiden az üyeli birim kurulmaz (oylama anlamsız); iki aşamalı kipte tek kişilik alt birim
    /// açılmaz, o kişi doğrudan finale kalır (bkz. <see cref="EomUnitPlan"/> Members.Count == 1).
    /// </summary>
    public static List<EomUnitPlan> BuildUnits(string scope, IReadOnlyList<EomDept> depts, IReadOnlyList<EomMember> members)
    {
        var ids = depts.Select(d => d.Id).ToHashSet();
        // Üst departmanı silinmiş/başka kiracıda olan departman ana departman sayılır.
        var children = depts.Where(d => d.ParentId is { } p && ids.Contains(p)).GroupBy(d => d.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.Name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true)).ToList());
        var roots = depts.Where(d => d.ParentId is not { } p || !ids.Contains(p))
            .OrderBy(d => d.Name, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true)).ToList();
        var direct = members.GroupBy(m => m.DepartmentId).ToDictionary(g => g.Key, g => g.Select(m => m.EmployeeId).Distinct().ToList());

        List<Guid> Subtree(Guid id)
        {
            var list = new List<Guid>(direct.GetValueOrDefault(id) ?? []);
            var seen = new HashSet<Guid> { id };
            var stack = new Stack<Guid>([id]);
            while (stack.Count > 0)
                foreach (var c in children.GetValueOrDefault(stack.Pop()) ?? [])
                    if (seen.Add(c.Id)) { list.AddRange(direct.GetValueOrDefault(c.Id) ?? []); stack.Push(c.Id); }
            return list.Distinct().ToList();
        }

        var units = new List<EomUnitPlan>();
        switch (scope)
        {
            case Eom.EachDepartment:
                foreach (var d in depts.OrderBy(d => d.Name, StringComparer.Ordinal))
                    if ((direct.GetValueOrDefault(d.Id) ?? []) is { Count: >= 2 } m)
                        units.Add(new(d.Id.ToString(), 1, d.Id, d.Name, d.HeadId, m, null));
                break;
            case Eom.TwoStage:
                foreach (var r in roots)
                {
                    var all = Subtree(r.Id);
                    if (all.Count < 2) continue;
                    var kids = children.GetValueOrDefault(r.Id) ?? [];
                    if (kids.Count == 0) { units.Add(new(r.Id.ToString(), 1, r.Id, r.Name, r.HeadId, all, null)); continue; }
                    var key = r.Id.ToString();
                    units.Add(new(key, 2, r.Id, r.Name, r.HeadId, all, null));
                    if ((direct.GetValueOrDefault(r.Id) ?? []) is { Count: >= 1 } own)
                        units.Add(new(key + ":direct", 1, r.Id, r.Name, r.HeadId, own, key));
                    foreach (var k in kids)
                        if (Subtree(k.Id) is { Count: >= 1 } sub)
                            units.Add(new(k.Id.ToString(), 1, k.Id, k.Name, k.HeadId, sub, key));
                }
                break;
            default:
                foreach (var r in roots)
                    if (Subtree(r.Id) is { Count: >= 2 } m)
                        units.Add(new(r.Id.ToString(), 1, r.Id, r.Name, r.HeadId, m, null));
                break;
        }
        return units;
    }

    /// <summary>
    /// Katsayılı sayım: puan = oy katsayılarının toplamı. Sıralama puan, sonra ham oy sayısına göre;
    /// ikisi de eşitse beraberlik (İK çözer). Oy yoksa kazanan yok.
    /// </summary>
    public static EomTally Tally(IEnumerable<EomBallot> ballots)
    {
        var ranking = ballots.GroupBy(b => b.Nominee)
            .Select(g => new EomStanding(g.Key, g.Sum(b => b.Weight), g.Count()))
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Votes).ThenBy(s => s.EmployeeId)
            .ToList();
        if (ranking.Count == 0) return new(ranking, null, []);
        var top = ranking[0];
        var tied = ranking.Where(s => s.Score == top.Score && s.Votes == top.Votes).Select(s => s.EmployeeId).ToList();
        return tied.Count > 1 ? new(ranking, null, tied) : new(ranking, top.EmployeeId, []);
    }

    /// <summary>
    /// Aday gösterme sonucu finalistler: en çok aday gösterilen ilk <paramref name="count"/> kişi; sınırda
    /// eşitlik varsa eşit olanların hepsi girer. Hiç aday gösterilmediyse tüm adaylar finalisttir.
    /// </summary>
    public static List<Guid> Finalists(IEnumerable<Guid> nominations, IReadOnlyCollection<Guid> eligible, int count)
    {
        var counts = nominations.Where(eligible.Contains).GroupBy(n => n).Select(g => (Id: g.Key, N: g.Count()))
            .OrderByDescending(x => x.N).ThenBy(x => x.Id).ToList();
        if (counts.Count == 0) return eligible.ToList();
        if (counts.Count <= count) return counts.Select(x => x.Id).ToList();
        var cutoff = counts[count - 1].N;
        return counts.Where(x => x.N >= cutoff).Select(x => x.Id).ToList();
    }

    /// <summary>Oy katsayısı: kişinin girdiği grupların en yükseği (çalışan / yönetici / İK).</summary>
    public static decimal Weight(EomConfig c, bool isManager, bool isHr)
    {
        var w = c.WeightEmployee;
        if (isManager) w = Math.Max(w, c.WeightManager);
        if (isHr) w = Math.Max(w, c.WeightHr);
        return w;
    }

    /// <summary>
    /// Oy/aday gösterme satırının kişi anahtarı. Kimliğin tutulduğu kiplerde açık kimlik; gizli kiplerde
    /// dönem tuzuyla HMAC (tuz dönem kapanınca silinir, eşleştirme geri döndürülemez).
    /// </summary>
    public static string VoterKey(EomConfig c, byte[]? salt, Guid unitId, Guid employeeId)
    {
        if (c.StoresIdentity) return employeeId.ToString("N");
        if (salt is null || salt.Length == 0) throw new InvalidOperationException("Dönem tuzu yok");
        var data = unitId.ToByteArray().Concat(employeeId.ToByteArray()).ToArray();
        return Convert.ToHexString(HMACSHA256.HashData(salt, data)).ToLowerInvariant();
    }

    /// <summary>Son çağrı: kapanıştan 24 saat (kısa oylamada sürenin yarısı) önce.</summary>
    public static DateTime LastCallAt(DateTime start, DateTime end)
        => end - TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(24).Ticks, (end - start).Ticks / 2));
}
