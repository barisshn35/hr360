using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;

namespace TimeShiftService.Services;

/// <summary>
/// Vardiya verisinin görünürlük/planlama kapsamı (KVKK: veri en aza).
///   * İK / şirket yöneticisi / ext-timeshift-manage: kiracının tamamı.
///   * Yönetici: başı olduğu departmanların çalışanları + kendisi (optimizer <c>apply</c> ile aynı kural).
///   * Çalışan: kendisi + ekip arkadaşları (aynı vardiya ekibi; hiçbir ekipte değilse aynı departman —
///     takas kuralı <c>SameTeamAsync</c> ile aynı tanım).
/// Saf kurallar birim testlidir; veritabanı okuyan yardımcılar paylaşılan tablolardan salt okur.
/// </summary>
public static class ShiftScope
{
    /// <summary>Yönetici bu kişiyi planlayabilir mi (başı olduğu bölümün çalışanı ya da kendisi).</summary>
    public static bool CanPlan(Guid? me, TsOps.PersonRow? person) =>
        me is { } m && person is not null && (person.HeadId == m || person.Id == m);

    /// <summary>Atama günü geçmişte mi (bugün serbest).</summary>
    public static bool IsPast(DateOnly date, DateOnly today) => date < today;

    /// <summary>
    /// Ekip arkadaşları (saf): ben bir ya da daha çok vardiya ekibindeysem o ekiplerin üyeleri; hiçbir
    /// ekipte değilsem departmanımdaki ve hiçbir ekipte olmayan kişiler (takas kuralı ile aynı). Ben dahil değil.
    /// </summary>
    public static HashSet<Guid> Peers(Guid me, IEnumerable<(Guid EmployeeId, Guid TeamId)> memberships, IEnumerable<TsOps.PersonRow> people)
    {
        var ms = memberships.ToList();
        var myTeams = ms.Where(m => m.EmployeeId == me).Select(m => m.TeamId).ToHashSet();
        if (myTeams.Count > 0)
            return ms.Where(m => myTeams.Contains(m.TeamId) && m.EmployeeId != me).Select(m => m.EmployeeId).ToHashSet();
        var list = people.ToList();
        var myDept = list.FirstOrDefault(p => p.Id == me)?.DepartmentId;
        if (myDept is null) return new();
        var inTeam = ms.Select(m => m.EmployeeId).ToHashSet();
        return list.Where(p => p.Id != me && p.DepartmentId == myDept && p.Status != "Terminated" && !inTeam.Contains(p.Id))
            .Select(p => p.Id).ToHashSet();
    }

    /// <summary>[from, to] aralığında etkin ekip üyelikleri.</summary>
    public static async Task<List<(Guid EmployeeId, Guid TeamId)>> MembershipsAsync(TimeShiftDbContext db, DateOnly from, DateOnly to, CancellationToken ct) =>
        (await db.ShiftTeamMembers.AsNoTracking()
            .Where(m => m.EffectiveFrom <= to && (m.EffectiveTo == null || m.EffectiveTo >= from))
            .Select(m => new { m.EmployeeId, m.ShiftTeamId }).ToListAsync(ct))
        .Select(m => (m.EmployeeId, m.ShiftTeamId)).ToList();

    /// <summary>Çalışanın ekip arkadaşları (veritabanından).</summary>
    public static async Task<HashSet<Guid>> PeersAsync(TimeShiftDbContext db, string tenant, Guid me, DateOnly from, DateOnly to, CancellationToken ct) =>
        Peers(me, await MembershipsAsync(db, from, to, ct), await TsOps.PeopleAsync(db, tenant, ct));

    /// <summary>Yöneticinin planlayabildiği kişiler (başı olduğu bölümler + kendisi).</summary>
    public static async Task<HashSet<Guid>> ManagedAsync(TimeShiftDbContext db, string tenant, Guid me, CancellationToken ct) =>
        (await TsOps.PeopleAsync(db, tenant, ct)).Where(p => CanPlan(me, p)).Select(p => p.Id).Append(me).ToHashSet();

    /// <summary>
    /// Vardiya ekipleri ve takvimlerinin görünürlüğü (KVKK: en aza indirme). İK için null (tümü).
    /// Yönetici: başı olduğu bölümün ekipleri, yönettiği kişilerin üye olduğu ekipler ve kendi ekipleri.
    /// Çalışan: yalnızca üyesi olduğu ekipler (ekip arkadaşları zaten aynı takvimi paylaşır).
    /// </summary>
    public static HashSet<Guid>? VisibleTeams(bool isHr, Guid? me, IEnumerable<(Guid TeamId, Guid? DepartmentId)> teams,
        IEnumerable<(Guid EmployeeId, Guid TeamId)> memberships, IEnumerable<TsOps.PersonRow> people)
    {
        if (isHr) return null;
        if (me is not { } m) return new HashSet<Guid>();
        var personList = people.ToList();
        var headed = personList.Where(p => p.HeadId == m && p.DepartmentId is not null).Select(p => p.DepartmentId!.Value).ToHashSet();
        var managed = personList.Where(p => p.HeadId == m).Select(p => p.Id).ToHashSet();
        var memberList = memberships.ToList();
        var visible = memberList.Where(x => x.EmployeeId == m || managed.Contains(x.EmployeeId)).Select(x => x.TeamId).ToHashSet();
        foreach (var t in teams)
            if (t.DepartmentId is { } d && headed.Contains(d)) visible.Add(t.TeamId);
        return visible;
    }
}
