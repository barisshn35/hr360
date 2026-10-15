using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

namespace TimeShiftService.Controllers;

/// <summary>
/// G6: vardiya takası. Akış: A talep eder (kendi atamasını B'nin atamasıyla değiştirme ya da B'ye
/// devretme) → B kabul/ret → A'nın bölüm başı (ekip planlayıcısı) ya da İK onay/ret → onayda
/// atamalar TEK işlemde değiştirilir. Kurallar talep ve onay anında denetlenir: aynı ekip, çakışma
/// yok, iki vardiya arası en az 11 saat, haftalık en çok 45 saat. İlgililere bildirim gider.
/// </summary>
[ApiController]
[Route("api/shift-swaps")]
[Authorize]
public class ShiftSwapsController : ControllerBase
{
    private readonly TimeShiftDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ITenantContext _tenant;

    public ShiftSwapsController(TimeShiftDbContext db, EmployeeDirectoryClient employees, ITenantContext tenant)
    {
        _db = db; _employees = employees; _tenant = tenant;
    }

    private string Tenant => _tenant.TenantSlug ?? "";
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-timeshift-manage");
    private string UserName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? "";

    private static DateOnly Today => ClockCore.WorkDate(DateTimeOffset.UtcNow);

    /* ------------------------------------------------------------------ yardımcılar */

    /// <summary>
    /// Aynı ekip: iki çalışan o tarihte aynı vardiya ekibinin etkin üyesi; ikisi de hiçbir ekipte
    /// değilse aynı departmanda olmaları yeterli.
    /// </summary>
    private async Task<bool> SameTeamAsync(Guid a, Guid b, DateOnly date, CancellationToken ct)
    {
        var memberships = await _db.ShiftTeamMembers.AsNoTracking()
            .Where(m => (m.EmployeeId == a || m.EmployeeId == b) && m.EffectiveFrom <= date && (m.EffectiveTo == null || m.EffectiveTo >= date))
            .Select(m => new { m.EmployeeId, m.ShiftTeamId }).ToListAsync(ct);
        var ta = memberships.Where(m => m.EmployeeId == a).Select(m => m.ShiftTeamId).ToHashSet();
        var tb = memberships.Where(m => m.EmployeeId == b).Select(m => m.ShiftTeamId).ToHashSet();
        if (ta.Count > 0 || tb.Count > 0) return ta.Overlaps(tb);
        var pa = await TsOps.PersonAsync(_db, Tenant, a, ct);
        var pb = await TsOps.PersonAsync(_db, Tenant, b, ct);
        return pa?.DepartmentId is { } da && da == pb?.DepartmentId;
    }

    private async Task<List<ShiftAssignment>> WindowAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await _db.ShiftAssignments.AsNoTracking().Include(x => x.Shift)
            .Where(x => x.EmployeeId == employeeId && x.Date >= from && x.Date <= to).ToListAsync(ct);

    private static ShiftInterval Interval(ShiftAssignment a, DateOnly? date = null) =>
        ShiftInterval.Of(a.Id, date ?? a.Date, a.Shift!.StartTime, a.Shift.EndTime, a.Shift.BreakMinutes);

    private WorkRuleSettings? _rules;
    private async Task<WorkRuleSettings> RulesAsync(CancellationToken ct) => _rules ??= await WorkRuleCheck.RulesAsync(_db, ct);

    /// <summary>
    /// Takas sonrası iki çalışanın programını kurallara göre değerlendirir (kiracının çalışma süresi
    /// ayarlarıyla, madde 66). "sameday", "overlap", "rest", "weekly" engeldir; "daily", "night",
    /// "consecutive" onaycıya uyarı olarak gösterilir.
    /// </summary>
    private async Task<List<RuleWarning>> EvaluateAsync(ShiftAssignment mine, ShiftAssignment? theirs, Guid requester, Guid target, CancellationToken ct)
    {
        var rules = await RulesAsync(ct);
        var from = (theirs is null || mine.Date < theirs.Date ? mine.Date : theirs.Date).AddDays(-8);
        var to = (theirs is null || mine.Date > theirs.Date ? mine.Date : theirs.Date).AddDays(8);
        var names = new Dictionary<Guid, string>();
        foreach (var id in new[] { requester, target })
            names[id] = (await TsOps.PersonAsync(_db, Tenant, id, ct))?.FirstName ?? "Çalışan";
        var result = new List<RuleWarning>();

        // Talep eden: kendi vardiyası çıkar, (varsa) karşı tarafın vardiyası girer.
        var aList = (await WindowAsync(requester, from, to, ct)).Where(x => x.Id != mine.Id && x.Shift is not null).Select(x => Interval(x)).ToList();
        if (theirs is not null)
        {
            if (aList.Any(x => DateOnly.FromDateTime(x.Start) == theirs.Date))
                result.Add(new RuleWarning("sameday", $"{names[requester]}: {theirs.Date:dd.MM.yyyy} günü zaten bir vardiyası var"));
            else
            {
                var n = Interval(theirs);
                aList.Add(n);
                result.AddRange(WorkRules.Check(aList, new[] { n }, rules, names[requester]));
            }
        }

        // Hedef: kendi vardiyası (varsa) çıkar, talep edenin vardiyası girer.
        var bList = (await WindowAsync(target, from, to, ct)).Where(x => x.Id != theirs?.Id && x.Shift is not null).Select(x => Interval(x)).ToList();
        if (bList.Any(x => DateOnly.FromDateTime(x.Start) == mine.Date))
            result.Add(new RuleWarning("sameday", $"{names[target]}: {mine.Date:dd.MM.yyyy} günü zaten bir vardiyası var"));
        else
        {
            var m = Interval(mine);
            bList.Add(m);
            result.AddRange(WorkRules.Check(bList, new[] { m }, rules, names[target]));
        }
        return result;
    }

    private static readonly string[] SwapBlocking = new[] { "sameday" }.Concat(WorkRules.Blocking).ToArray();

    /// <summary>Takas sonrası iki çalışanın programını kurallara göre denetler; engelleyici ihlal varsa açıklama.</summary>
    private async Task<string?> ValidateAsync(ShiftAssignment mine, ShiftAssignment? theirs, Guid requester, Guid target, CancellationToken ct)
    {
        var all = await EvaluateAsync(mine, theirs, requester, target, ct);
        return all.FirstOrDefault(w => SwapBlocking.Contains(w.Code))?.Message;
    }

    private async Task<Guid?> HeadOfAsync(Guid employeeId, CancellationToken ct) =>
        (await TsOps.PersonAsync(_db, Tenant, employeeId, ct))?.HeadId;

    /// <summary>Onaylayabilir mi: İK ya da talep edenin bölüm başı olan yönetici; taraflar kendi takasını onaylayamaz.</summary>
    private async Task<bool> CanApproveAsync(ShiftSwapRequest s, Guid? me, CancellationToken ct)
    {
        if (me is { } m && (m == s.RequesterEmployeeId || m == s.TargetEmployeeId)) return false;
        if (IsHr) return true;
        if (!User.IsInRole("manager") || me is null) return false;
        return await CanDecideAsHeadAsync(s, me.Value, ct);
    }

    /// <summary>Yönetici kuralı: karar veren taraflardan biri değil ve talep edenin bölüm başı.</summary>
    internal static bool IsHeadDecider(ShiftSwapRequest s, Guid decider, Guid? requesterHead) =>
        decider != s.RequesterEmployeeId && decider != s.TargetEmployeeId && requesterHead == decider;

    /// <summary>
    /// Sohbet (iç uç) yolu: rol bilgisi yoktur; karar veren, bot tarafından doğrulanmış çalışan
    /// kimliğidir ve yalnızca bölüm başı kuralı uygulanır (web'deki yönetici yolu ile aynı).
    /// </summary>
    internal async Task<bool> CanDecideAsHeadAsync(ShiftSwapRequest s, Guid decider, CancellationToken ct)
    {
        if (decider == s.RequesterEmployeeId || decider == s.TargetEmployeeId) return false;
        return IsHeadDecider(s, decider, await HeadOfAsync(s.RequesterEmployeeId, ct));
    }

    private async Task<object> DtoAsync(ShiftSwapRequest s, Dictionary<Guid, ShiftAssignment> assignments, Dictionary<Guid, string> names, Guid? me, CancellationToken ct) => new
    {
        s.Id, s.Status, s.Note, s.RejectReason, s.CreatedAt, s.PeerRespondedAt, s.DecidedAt, s.DecidedBy,
        s.RequesterEmployeeId, requesterName = names.GetValueOrDefault(s.RequesterEmployeeId),
        s.TargetEmployeeId, targetName = names.GetValueOrDefault(s.TargetEmployeeId),
        requesterShift = ShiftDto(assignments.GetValueOrDefault(s.RequesterAssignmentId)),
        targetShift = s.TargetAssignmentId is { } t ? ShiftDto(assignments.GetValueOrDefault(t)) : null,
        giveAway = s.TargetAssignmentId is null,
        canRespond = me == s.TargetEmployeeId && s.Status == SwapStatus.PendingPeer,
        canApprove = s.Status == SwapStatus.PendingApproval && await CanApproveAsync(s, me, ct),
        canCancel = me == s.RequesterEmployeeId && s.Status is SwapStatus.PendingPeer or SwapStatus.PendingApproval,
        // Madde 66: onaycıya kural uyarıları (engelleyiciler onayda yeniden denetlenir ve takası reddeder).
        ruleWarnings = s.Status == SwapStatus.PendingApproval && await CanApproveAsync(s, me, ct)
            && assignments.GetValueOrDefault(s.RequesterAssignmentId) is { Shift: not null } mine
            && (s.TargetAssignmentId is null || assignments.GetValueOrDefault(s.TargetAssignmentId.Value)?.Shift is not null)
            ? (await EvaluateAsync(mine, s.TargetAssignmentId is { } ta ? assignments[ta] : null, s.RequesterEmployeeId, s.TargetEmployeeId, ct))
                .Select(w => new { w.Code, w.Message, blocking = SwapBlocking.Contains(w.Code) }).ToList()
            : null,
    };

    private static object? ShiftDto(ShiftAssignment? a) => a?.Shift is null ? null : new
    {
        assignmentId = a.Id, a.Date, shiftId = a.ShiftId, name = a.Shift.Name, startTime = a.Shift.StartTime, endTime = a.Shift.EndTime,
    };

    /* ------------------------------------------------------------------ uçlar */

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? scope, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var q = _db.ShiftSwapRequests.AsNoTracking();
        if (scope == "approvals" || scope == "all")
        {
            if (!IsHr && !User.IsInRole("manager")) return Forbid();
            if (scope == "approvals") q = q.Where(s => s.Status == SwapStatus.PendingApproval);
        }
        else
        {
            if (me is null) return Ok(Array.Empty<object>());
            q = q.Where(s => s.RequesterEmployeeId == me || s.TargetEmployeeId == me);
        }
        var rows = await q.OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);
        if (!IsHr && scope is "approvals" or "all")
        {
            // Yönetici yalnızca kendi bölümünün takaslarını görür.
            var heads = new Dictionary<Guid, Guid?>();
            foreach (var r in rows.Select(r => r.RequesterEmployeeId).Distinct()) heads[r] = await HeadOfAsync(r, ct);
            rows = rows.Where(r => heads[r.RequesterEmployeeId] == me).ToList();
        }
        var aIds = rows.Select(r => r.RequesterAssignmentId).Concat(rows.Where(r => r.TargetAssignmentId != null).Select(r => r.TargetAssignmentId!.Value)).ToList();
        var assignments = await _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift).Where(a => aIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var names = (await TsOps.PeopleAsync(_db, Tenant, ct)).ToDictionary(p => p.Id, p => p.FullName);
        var list = new List<object>();
        foreach (var r in rows) list.Add(await DtoAsync(r, assignments, names, me, ct));
        return Ok(list);
    }

    /// <summary>
    /// Takas için seçilebilecek ekip arkadaşları: aynı vardiya ekibinin üyeleri (ekipsizse aynı
    /// departman; <see cref="SameTeamAsync"/> ile aynı tanım), önümüzdeki 60 gün. Yalnızca kimlik ve ad döner.
    /// </summary>
    [HttpGet("peers")]
    public async Task<IActionResult> Peers(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var peers = await ShiftScope.PeersAsync(_db, Tenant, me.Value, Today, Today.AddDays(60), ct);
        var people = await TsOps.PeopleAsync(_db, Tenant, ct);
        return Ok(people.Where(p => peers.Contains(p.Id) && p.Status != "Terminated")
            .OrderBy(p => p.FullName, StringComparer.Ordinal)
            .Select(p => new { id = p.Id, fullName = p.FullName }));
    }

    public record CreateSwapInput(Guid MyAssignmentId, Guid TargetEmployeeId, Guid? TargetAssignmentId, string? Note);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSwapInput b, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return Forbid();
        if (b.TargetEmployeeId == me) return BadRequest(new { message = "Kendinizle takas yapamazsınız" });
        if (b.Note is { Length: > 300 }) return BadRequest(new { message = "Not en fazla 300 karakter olabilir" });
        var mine = await _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift).FirstOrDefaultAsync(a => a.Id == b.MyAssignmentId, ct);
        if (mine is null || mine.EmployeeId != me) return NotFound(new { message = "Vardiya atamanız bulunamadı" });
        if (mine.Date < Today) return BadRequest(new { message = "Geçmiş vardiya takas edilemez" });
        ShiftAssignment? theirs = null;
        if (b.TargetAssignmentId is { } tid)
        {
            theirs = await _db.ShiftAssignments.AsNoTracking().Include(a => a.Shift).FirstOrDefaultAsync(a => a.Id == tid, ct);
            if (theirs is null || theirs.EmployeeId != b.TargetEmployeeId) return NotFound(new { message = "Karşı tarafın vardiya ataması bulunamadı" });
            if (theirs.Date < Today) return BadRequest(new { message = "Geçmiş vardiya takas edilemez" });
        }
        if (!await SameTeamAsync(me.Value, b.TargetEmployeeId, mine.Date, ct))
            return BadRequest(new { message = "Takas yalnızca aynı ekipteki çalışanlar arasında yapılabilir", code = "rule_violation" });
        var busy = new[] { mine.Id }.Concat(theirs is null ? Array.Empty<Guid>() : new[] { theirs.Id }).ToList();
        if (await _db.ShiftSwapRequests.AnyAsync(s => (s.Status == SwapStatus.PendingPeer || s.Status == SwapStatus.PendingApproval)
                && (busy.Contains(s.RequesterAssignmentId) || (s.TargetAssignmentId != null && busy.Contains(s.TargetAssignmentId.Value))), ct))
            return Conflict(new { message = "Bu vardiya için bekleyen bir takas talebi var" });
        if (await ValidateAsync(mine, theirs, me.Value, b.TargetEmployeeId, ct) is { } reason)
            return BadRequest(new { message = reason, code = "rule_violation" });

        var s = new ShiftSwapRequest
        {
            RequesterEmployeeId = me.Value, RequesterAssignmentId = mine.Id, TargetEmployeeId = b.TargetEmployeeId,
            TargetAssignmentId = theirs?.Id, Note = string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim(),
        };
        _db.ShiftSwapRequests.Add(s);
        await _db.SaveChangesAsync(ct);
        var requester = await TsOps.PersonAsync(_db, Tenant, me.Value, ct);
        await TsOps.NotifyAsync(_db, Tenant, b.TargetEmployeeId, "Vardiya takas talebi",
            theirs is null
                ? $"{requester?.FullName} {mine.Date:dd.MM.yyyy} {mine.Shift!.Name} vardiyasını size devretmek istiyor. Vardiya ekranından yanıtlayın."
                : $"{requester?.FullName} {mine.Date:dd.MM.yyyy} {mine.Shift!.Name} vardiyasını sizin {theirs.Date:dd.MM.yyyy} {theirs.Shift!.Name} vardiyanızla değiştirmek istiyor.",
            "shift.swap.request", ct);
        return Ok(new { s.Id, s.Status });
    }

    public record RespondInput(bool Accept);

    [HttpPost("{id:guid}/respond")]
    public async Task<IActionResult> Respond(Guid id, [FromBody] RespondInput b, CancellationToken ct) =>
        await RespondCoreAsync(await _employees.FindMyEmployeeIdAsync(ct), id, b.Accept, null, ct);

    /// <summary>Hedef kişinin kabul/reddi (web ve sohbet iç ucu). chatPlatform doluysa sohbet denetim kaydı yazılır.</summary>
    internal async Task<IActionResult> RespondCoreAsync(Guid? me, Guid id, bool accept, string? chatPlatform, CancellationToken ct)
    {
        var s = await _db.ShiftSwapRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || me is null || s.TargetEmployeeId != me) return NotFound();
        if (s.Status != SwapStatus.PendingPeer) return Conflict(new { message = "Talep yanıt beklemiyor", code = "not_pending" });
        s.Status = accept ? SwapStatus.PendingApproval : SwapStatus.Declined;
        s.PeerRespondedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var target = await TsOps.PersonAsync(_db, Tenant, me.Value, ct);
        await TsOps.NotifyAsync(_db, Tenant, s.RequesterEmployeeId, accept ? "Takas talebiniz kabul edildi" : "Takas talebiniz reddedildi",
            accept ? $"{target?.FullName} takas talebinizi kabul etti; yönetici onayı bekleniyor." : $"{target?.FullName} takas talebinizi kabul etmedi.",
            accept ? "shift.swap.accepted" : "shift.swap.declined", ct);
        if (accept && await HeadOfAsync(s.RequesterEmployeeId, ct) is { } head && head != s.RequesterEmployeeId && head != s.TargetEmployeeId)
            await TsOps.NotifyAsync(_db, Tenant, head, "Onay bekleyen vardiya takası",
                "Ekibinizde iki çalışan vardiya takasında anlaştı; Vardiya ekranındaki takas onaylarından karar verin.", "shift.swap.approval", ct);
        if (chatPlatform is not null)
            await TsOps.ChatAuditAsync(_db, Tenant, me.Value, "ShiftSwapRequest", s.Id.ToString(), accept ? "PeerAccepted" : "PeerDeclined", new { }, chatPlatform, ct);
        return Ok(new { s.Id, s.Status });
    }

    public record DecideInput(bool Approve, string? Reason);

    [HttpPost("{id:guid}/decide")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideInput b, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        return await DecideCoreAsync(id, b, s => CanApproveAsync(s, me, ct), () => UserName, null, ct);
    }

    /// <summary>
    /// Onay/ret (web ve sohbet iç ucu). Yetki kuralı çağırandan gelir (web: İK ya da bölüm başı yönetici;
    /// sohbet: bölüm başı). chatActor doluysa (çalışan, platform) sohbet denetim kaydı yazılır.
    /// </summary>
    internal async Task<IActionResult> DecideCoreAsync(Guid id, DecideInput b, Func<ShiftSwapRequest, Task<bool>> canApprove, Func<string> decidedBy,
        (Guid EmployeeId, string Platform)? chatActor, CancellationToken ct)
    {
        var s = await _db.ShiftSwapRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        if (!await canApprove(s)) return StatusCode(403, new { message = "Bu takası onaylama yetkiniz yok" });
        if (s.Status != SwapStatus.PendingApproval) return Conflict(new { message = "Takas onay beklemiyor", code = "not_pending" });
        if (b.Reason is { Length: > 300 }) return BadRequest(new { message = "Gerekçe en fazla 300 karakter olabilir" });

        if (!b.Approve)
        {
            s.Status = SwapStatus.Rejected;
            s.RejectReason = string.IsNullOrWhiteSpace(b.Reason) ? "Yönetici onaylamadı" : b.Reason.Trim();
            s.DecidedAt = DateTimeOffset.UtcNow;
            s.DecidedBy = decidedBy();
            await _db.SaveChangesAsync(ct);
            await NotifyBothAsync(s, "Vardiya takası onaylanmadı", $"Takas talebi onaylanmadı: {s.RejectReason}", ct);
            if (chatActor is { } ca0)
                await TsOps.ChatAuditAsync(_db, Tenant, ca0.EmployeeId, "ShiftSwapRequest", s.Id.ToString(), "Rejected", new { rule = false }, ca0.Platform, ct);
            return Ok(new { s.Id, s.Status, s.RejectReason });
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var ids = new List<Guid> { s.RequesterAssignmentId };
        if (s.TargetAssignmentId is { } t) ids.Add(t);
        // Satırları kilitle: eşzamanlı başka bir değişiklik takası bozmasın.
        await _db.Database.ExecuteSqlRawAsync("SELECT 1 FROM timeshift_assignments WHERE \"Id\" = ANY({0}) FOR UPDATE", new object[] { ids.ToArray() }, ct);
        var mine = await _db.ShiftAssignments.Include(a => a.Shift).FirstOrDefaultAsync(a => a.Id == s.RequesterAssignmentId, ct);
        var theirs = s.TargetAssignmentId is { } tt ? await _db.ShiftAssignments.Include(a => a.Shift).FirstOrDefaultAsync(a => a.Id == tt, ct) : null;

        string? reason = null;
        if (mine is null || mine.EmployeeId != s.RequesterEmployeeId || (s.TargetAssignmentId is not null && (theirs is null || theirs.EmployeeId != s.TargetEmployeeId)))
            reason = "Vardiya atamaları talepten sonra değişmiş";
        else if (mine.Date < Today || (theirs is not null && theirs.Date < Today))
            reason = "Geçmiş vardiya takas edilemez";
        else if (!await SameTeamAsync(s.RequesterEmployeeId, s.TargetEmployeeId, mine.Date, ct))
            reason = "Çalışanlar artık aynı ekipte değil";
        else reason = await ValidateAsync(mine, theirs, s.RequesterEmployeeId, s.TargetEmployeeId, ct);

        if (reason is not null)
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            var fresh = await _db.ShiftSwapRequests.FirstAsync(x => x.Id == id, ct);
            fresh.Status = SwapStatus.Rejected;
            fresh.RejectReason = reason;
            fresh.DecidedAt = DateTimeOffset.UtcNow;
            fresh.DecidedBy = decidedBy();
            await _db.SaveChangesAsync(ct);
            await NotifyBothAsync(fresh, "Vardiya takası reddedildi", $"Takas kurallara uymadığı için reddedildi: {reason}", ct);
            if (chatActor is { } ca1)
                await TsOps.ChatAuditAsync(_db, Tenant, ca1.EmployeeId, "ShiftSwapRequest", fresh.Id.ToString(), "Rejected", new { rule = true }, ca1.Platform, ct);
            return BadRequest(new { message = reason, code = "rule_violation", status = fresh.Status });
        }

        // Atomik değişim. Aynı gündeyse vardiya kimlikleri, farklı günlerdeyse çalışanlar yer değiştirir
        // (çalışan+gün tekil indeksi ara adımda çakışmasın diye).
        if (theirs is not null && theirs.Date == mine!.Date)
            (mine.ShiftId, theirs.ShiftId) = (theirs.ShiftId, mine.ShiftId);
        else
        {
            mine!.EmployeeId = s.TargetEmployeeId;
            if (theirs is not null) theirs.EmployeeId = s.RequesterEmployeeId;
        }
        s.Status = SwapStatus.Approved;
        s.DecidedAt = DateTimeOffset.UtcNow;
        s.DecidedBy = decidedBy();
        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return Conflict(new { message = "Takas uygulanamadı: atamalar eşzamanlı değişti; yeniden deneyin" });
        }
        await NotifyBothAsync(s, "Vardiya takası onaylandı", "Vardiya takasınız onaylandı; güncel programınızı Vardiya ekranında görebilirsiniz.", ct);
        if (chatActor is { } ca2)
            await TsOps.ChatAuditAsync(_db, Tenant, ca2.EmployeeId, "ShiftSwapRequest", s.Id.ToString(), "Approved", new { rule = false }, ca2.Platform, ct);
        return Ok(new { s.Id, s.Status });
    }

    private async Task NotifyBothAsync(ShiftSwapRequest s, string subject, string body, CancellationToken ct)
    {
        await TsOps.NotifyAsync(_db, Tenant, s.RequesterEmployeeId, subject, body, "shift.swap.decision", ct);
        await TsOps.NotifyAsync(_db, Tenant, s.TargetEmployeeId, subject, body, "shift.swap.decision", ct);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var s = await _db.ShiftSwapRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || me is null || s.RequesterEmployeeId != me) return NotFound();
        if (s.Status is not (SwapStatus.PendingPeer or SwapStatus.PendingApproval)) return Conflict(new { message = "Talep artık iptal edilemez" });
        s.Status = SwapStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        await TsOps.NotifyAsync(_db, Tenant, s.TargetEmployeeId, "Vardiya takası iptal edildi", "Size gelen vardiya takas talebi geri çekildi.", "shift.swap.cancelled", ct);
        return Ok(new { s.Id, s.Status });
    }
}
