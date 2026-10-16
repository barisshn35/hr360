using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Infrastructure.Cards;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Grup kartı (yenilik haritası #26). Herkes bir çalışma arkadaşı için
 * kart açabilir; diğerleri imzalar. Kart teslim edilene kadar alıcı
 * kartı göremez (liste ve ayrıntı ona kapalıdır).
 * ==================================================================== */
[Route("api/cards")]
[Authorize]
public class CardsController : AppController
{
    public static readonly string[] Occasions = ["Birthday", "Farewell", "Welcome", "Anniversary", "Congrats", "GetWell", "Other"];
    public static readonly string[] Colors = ["yellow", "pink", "blue", "green", "violet", "orange"];
    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public CardsController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    /// <summary>Alıcı, teslim edilmemiş kendi kartını göremez (saf kural).</summary>
    public static bool CanView(GroupCard c, Guid? me) => c.DeliveredAt is not null || c.RecipientEmployeeId != me;

    private object Summary(GroupCard c, int signers, bool signedByMe, Guid? me) => new
    {
        c.Id, c.RecipientEmployeeId, c.RecipientName, c.Occasion, c.Title, c.DeliverAt, c.DeliveredAt, c.CreatedByName, c.CreatedAt,
        signers, signedByMe, forMe = c.RecipientEmployeeId == me,
        canManage = c.CreatedByUserId == Me.UserId || Me.IsHr,
    };

    /// <summary>Açık kartlar (imzalanabilir), son 60 günde teslim edilenler ve bana gelenler.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = (await MyPersonAsync(ct))?.Id;
        var since = DateTime.UtcNow.AddDays(-60);
        var cards = await _db.Cards.AsNoTracking()
            .Where(c => (c.DeliveredAt == null && c.RecipientEmployeeId != me) || (c.DeliveredAt != null && (c.DeliveredAt >= since || c.RecipientEmployeeId == me)))
            .OrderBy(c => c.DeliveredAt != null).ThenBy(c => c.DeliverAt).Take(200).ToListAsync(ct);
        var ids = cards.Select(c => c.Id).ToList();
        var sigs = await _db.CardSignatures.AsNoTracking().Where(s => ids.Contains(s.CardId)).Select(s => new { s.CardId, s.EmployeeId }).ToListAsync(ct);
        var by = sigs.ToLookup(s => s.CardId);
        return Ok(cards.Select(c => Summary(c, by[c.Id].Count(), me is not null && by[c.Id].Any(s => s.EmployeeId == me), me)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var me = (await MyPersonAsync(ct))?.Id;
        var c = await _db.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanView(c, me)) return NotFound();
        var sigs = await _db.CardSignatures.AsNoTracking().Where(s => s.CardId == id).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return Ok(new
        {
            card = Summary(c, sigs.Count, sigs.Any(s => s.EmployeeId == me), me),
            signatures = sigs.Select(s => new { s.Id, s.EmployeeId, s.Name, s.Message, s.Color, s.CreatedAt, mine = s.EmployeeId == me }),
        });
    }

    public sealed record CardInput(Guid RecipientEmployeeId, string Occasion, string Title, DateTime DeliverAt, bool NotifyTeam = true);

    [HttpPost]
    public async Task<IActionResult> Create(CardInput b, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Kart açmak için çalışan kaydınız olmalı" });
        if (!Occasions.Contains(b.Occasion)) return BadRequest(new { message = "Geçersiz kart türü" });
        if ((b.Title ?? "").Trim().Length is < 3 or > 120) return BadRequest(new { message = "Başlık 3-120 karakter olmalı" });
        if (b.DeliverAt <= DateTime.UtcNow || b.DeliverAt > DateTime.UtcNow.AddDays(60)) return BadRequest(new { message = "Teslim zamanı önümüzdeki 60 gün içinde olmalı" });
        if (b.RecipientEmployeeId == me.Id) return BadRequest(new { message = "Kendinize kart açamazsınız" });
        var to = await People.FindAsync(Tenant, b.RecipientEmployeeId, ct);
        if (to is null) return NotFound(new { message = "Çalışan bulunamadı" });
        var open = await _db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.RecipientEmployeeId == to.Id && c.Occasion == b.Occasion && c.DeliveredAt == null, ct);
        if (open is not null) return Conflict(new { message = "Bu kişi için aynı türde açık bir kart var; onu imzalayabilirsiniz.", id = open.Id });
        var card = new GroupCard
        {
            RecipientEmployeeId = to.Id, RecipientName = to.Name, Occasion = b.Occasion, Title = b.Title.Trim(), DeliverAt = b.DeliverAt,
            CreatedByEmployeeId = me.Id, CreatedByUserId = Me.UserId, CreatedByName = me.Name,
        };
        _db.Cards.Add(card);
        await _db.SaveChangesAsync(ct);
        if (b.NotifyTeam && to.DepartmentId is { } dept)
            foreach (var p in (await People.ListAsync(Tenant, ct)).Where(p => p.DepartmentId == dept && p.Id != to.Id && p.Id != me.Id))
                await _notify.InAppAsync(Tenant, p.Id, $"{to.Name} için kart imzalanıyor", $"{me.Name} \"{card.Title}\" kartını açtı. Bir not bırakın; kart {to.Name}'a teslim edilene kadar gizli kalır.", "cards.invite", ct);
        return Ok(Summary(card, 0, false, me.Id));
    }

    public sealed record SignInput(string Message, string? Color);

    [HttpPut("{id:guid}/sign")]
    public async Task<IActionResult> Sign(Guid id, SignInput b, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "İmza için çalışan kaydınız olmalı" });
        var c = await _db.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanView(c, me.Id)) return NotFound();
        if (c.DeliveredAt is not null) return BadRequest(new { message = "Kart teslim edildi; artık imzalanamaz" });
        var msg = (b.Message ?? "").Trim();
        if (msg.Length is < 1 or > 500) return BadRequest(new { message = "Not 1-500 karakter olmalı" });
        var color = Colors.Contains(b.Color) ? b.Color! : "yellow";
        var s = await _db.CardSignatures.FirstOrDefaultAsync(x => x.CardId == id && x.EmployeeId == me.Id, ct);
        if (s is null) _db.CardSignatures.Add(s = new CardSignature { CardId = id, EmployeeId = me.Id, Name = me.Name });
        (s.Message, s.Color, s.UpdatedAt) = (msg, color, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.Id, s.Message, s.Color });
    }

    [HttpDelete("{id:guid}/sign")]
    public async Task<IActionResult> Unsign(Guid id, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return NoContent();
        var c = await _db.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || c.DeliveredAt is not null) return BadRequest(new { message = "Teslim edilmiş karttan imza silinemez" });
        var s = await _db.CardSignatures.FirstOrDefaultAsync(x => x.CardId == id && x.EmployeeId == me.Id, ct);
        if (s is not null) { _db.CardSignatures.Remove(s); await _db.SaveChangesAsync(ct); }
        return NoContent();
    }

    /// <summary>Açan kişi ya da İK: kartı hemen teslim eder.</summary>
    [HttpPost("{id:guid}/deliver")]
    public async Task<IActionResult> Deliver(Guid id, CancellationToken ct)
    {
        var c = await _db.Cards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.CreatedByUserId != Me.UserId && !Me.IsHr) return Forbid();
        if (c.DeliveredAt is not null) return BadRequest(new { message = "Kart zaten teslim edildi" });
        var signers = await _db.CardSignatures.CountAsync(s => s.CardId == id, ct);
        if (signers == 0) return BadRequest(new { message = "Teslim için en az bir imza gerekli" });
        c.DeliveredAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CardWorker.NotifyDeliveredAsync(_notify, Tenant, c.RecipientEmployeeId, c.Title, c.Id, signers, ct);
        return Ok(new { c.Id, c.DeliveredAt });
    }

    /// <summary>İK: teslim zamanı gelmiş kartları zamanlayıcıyı beklemeden teslim eder.</summary>
    [HttpPost("run")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Run(CancellationToken ct)
        => Ok(new { delivered = await HttpContext.RequestServices.GetRequiredService<CardWorker>().RunOnceAsync(ct, Tenant) });

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var c = await _db.Cards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.CreatedByUserId != Me.UserId && !Me.IsHr) return Forbid();
        _db.Cards.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
