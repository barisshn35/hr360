using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Takdir puanları ve ödül kataloğu (yenilik haritası #25). İK açar ve
 * aylık bütçeyi belirler; çalışan takdirle puan gönderir, biriken puanı
 * katalogdaki ödüllerle takas eder. Takasta puan hemen düşülür; İK
 * teslim eder ya da reddeder (ret puanı ve stoğu iade eder).
 * ==================================================================== */
[Route("api/recognition")]
[Authorize]
public class RecognitionController : AppController
{
    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public RecognitionController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await RecognitionPoints.SettingsAsync(_db, ct);
        return Ok(new { s.Enabled, s.MonthlyAllowance, s.MaxPerKudos });
    }

    public sealed record SettingsInput(bool Enabled, int MonthlyAllowance, int MaxPerKudos);

    [HttpPut("settings")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PutSettings(SettingsInput b, CancellationToken ct)
    {
        if (b.MonthlyAllowance is < 1 or > 100000) return BadRequest(new { message = "Aylık bütçe 1-100000 olmalı" });
        if (b.MaxPerKudos < 1 || b.MaxPerKudos > b.MonthlyAllowance) return BadRequest(new { message = "Takdir başına üst sınır 1 ile aylık bütçe arasında olmalı" });
        var s = await _db.RecognitionSettings.FirstOrDefaultAsync(ct);
        if (s is null) _db.RecognitionSettings.Add(s = new RecognitionSettings());
        (s.Enabled, s.MonthlyAllowance, s.MaxPerKudos, s.UpdatedAt) = (b.Enabled, b.MonthlyAllowance, b.MaxPerKudos, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return Ok(new { s.Enabled, s.MonthlyAllowance, s.MaxPerKudos });
    }

    private static string ReasonLabel(PointsEntry e) => e.Reason switch
    {
        "Kudos" => $"Takdir: {e.Note}",
        "KudosReversed" => "Takdir kaldırıldı",
        "Redeem" => $"Ödül: {e.Note}",
        "Refund" => $"İade: {e.Note}",
        "Grant" => $"İK: {e.Note}",
        _ => e.Reason,
    };

    /// <summary>Çalışan: bakiye, bu ay kalan bütçe ve son hareketler.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var s = await RecognitionPoints.SettingsAsync(_db, ct);
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { s.Enabled, balance = 0, givenThisMonth = 0, remaining = 0, s.MonthlyAllowance, s.MaxPerKudos, history = Array.Empty<object>() });
        var given = await RecognitionPoints.GivenThisMonthAsync(_db, me.Id, ct);
        var history = await _db.PointsLedger.AsNoTracking().Where(x => x.EmployeeId == me.Id).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct);
        return Ok(new
        {
            s.Enabled, balance = await RecognitionPoints.BalanceAsync(_db, me.Id, ct), givenThisMonth = given,
            remaining = Math.Max(0, s.MonthlyAllowance - given), s.MonthlyAllowance, s.MaxPerKudos,
            history = history.Select(x => new { x.Id, x.Delta, x.Reason, label = ReasonLabel(x), x.CreatedAt }),
        });
    }

    /* ------------------------------------------------------------ katalog */

    [HttpGet("rewards")]
    public async Task<IActionResult> Rewards([FromQuery] bool all = false, CancellationToken ct = default)
    {
        var q = _db.Rewards.AsNoTracking();
        if (!(all && Me.IsHr)) q = q.Where(r => r.Active);
        return Ok(await q.OrderBy(r => r.Cost).Select(r => new { r.Id, r.Title, r.Description, r.Cost, r.Stock, r.Active }).ToListAsync(ct));
    }

    public sealed record RewardInput(string Title, string? Description, int Cost, int? Stock, bool Active = true);

    private IActionResult? Validate(RewardInput b)
    {
        if ((b.Title ?? "").Trim().Length is < 2 or > 120) return BadRequest(new { message = "Ödül adı 2-120 karakter olmalı" });
        if (b.Description is { Length: > 1000 }) return BadRequest(new { message = "Açıklama en fazla 1000 karakter" });
        if (b.Cost is < 1 or > 1_000_000) return BadRequest(new { message = "Puan değeri 1 ya da daha fazla olmalı" });
        if (b.Stock is < 0) return BadRequest(new { message = "Stok negatif olamaz" });
        return null;
    }

    [HttpPost("rewards")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateReward(RewardInput b, CancellationToken ct)
    {
        if (Validate(b) is { } err) return err;
        var r = new Reward { Title = b.Title.Trim(), Description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim(), Cost = b.Cost, Stock = b.Stock, Active = b.Active };
        _db.Rewards.Add(r);
        await _db.SaveChangesAsync(ct);
        return Ok(new { r.Id, r.Title, r.Description, r.Cost, r.Stock, r.Active });
    }

    [HttpPut("rewards/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateReward(Guid id, RewardInput b, CancellationToken ct)
    {
        var r = await _db.Rewards.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (Validate(b) is { } err) return err;
        (r.Title, r.Description, r.Cost, r.Stock, r.Active) = (b.Title.Trim(), string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim(), b.Cost, b.Stock, b.Active);
        await _db.SaveChangesAsync(ct);
        return Ok(new { r.Id, r.Title, r.Description, r.Cost, r.Stock, r.Active });
    }

    /// <summary>Takas: puan hemen düşülür, stok bir azalır; İK teslim eder.</summary>
    [HttpPost("rewards/{id:guid}/redeem")]
    public async Task<IActionResult> Redeem(Guid id, CancellationToken ct)
    {
        var s = await RecognitionPoints.SettingsAsync(_db, ct);
        if (!s.Enabled) return BadRequest(new { message = "Takdir puanları kapalı" });
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Takas için çalışan kaydınız olmalı" });
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await RecognitionPoints.LockAsync(_db, Tenant, me.Id, ct);
        // Stok yarışına karşı ödül satırı da kilitlenir.
        var r = await _db.Rewards.FromSqlRaw("""SELECT * FROM engagement_rewards WHERE "Id" = {0} FOR UPDATE""", id).FirstOrDefaultAsync(ct);
        if (r is null || !r.Active) return NotFound(new { message = "Ödül bulunamadı" });
        if (r.Stock is 0) return BadRequest(new { message = "Bu ödül tükendi" });
        var balance = await RecognitionPoints.BalanceAsync(_db, me.Id, ct);
        if (balance < r.Cost) return BadRequest(new { message = $"Puanınız yetmiyor ({balance}/{r.Cost})" });
        var red = new RewardRedemption { RewardId = r.Id, RewardTitle = r.Title, EmployeeId = me.Id, Name = me.Name, Cost = r.Cost };
        _db.RewardRedemptions.Add(red);
        _db.PointsLedger.Add(new PointsEntry { EmployeeId = me.Id, Delta = -r.Cost, Reason = "Redeem", RefId = red.Id, Note = r.Title });
        if (r.Stock is { } st) r.Stock = st - 1;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new { red.Id, red.Status, balance = balance - r.Cost });
    }

    [HttpGet("redemptions/me")]
    public async Task<IActionResult> MyRedemptions(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        return Ok(await _db.RewardRedemptions.AsNoTracking().Where(x => x.EmployeeId == me.Id).OrderByDescending(x => x.CreatedAt).Take(50)
            .Select(x => new { x.Id, x.RewardTitle, x.Cost, x.Status, x.Note, x.CreatedAt, x.DecidedAt }).ToListAsync(ct));
    }

    [HttpGet("redemptions")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Redemptions([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.RewardRedemptions.AsNoTracking();
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        return Ok(await q.OrderByDescending(x => x.CreatedAt).Take(200)
            .Select(x => new { x.Id, x.RewardTitle, x.EmployeeId, x.Name, x.Cost, x.Status, x.Note, x.CreatedAt, x.DecidedAt }).ToListAsync(ct));
    }

    public sealed record DecisionInput(bool Fulfilled, string? Note);

    /// <summary>İK: teslim edildi ya da reddedildi (ret: puan ve stok iade).</summary>
    [HttpPost("redemptions/{id:guid}/decide")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Decide(Guid id, DecisionInput b, CancellationToken ct)
    {
        if (!b.Fulfilled && string.IsNullOrWhiteSpace(b.Note)) return BadRequest(new { message = "Ret için açıklama yazın" });
        if (b.Note is { Length: > 500 }) return BadRequest(new { message = "Açıklama en fazla 500 karakter" });
        var red = await _db.RewardRedemptions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (red is null) return NotFound();
        if (red.Status != "Pending") return BadRequest(new { message = "Bu talep zaten sonuçlandı" });
        (red.Status, red.Note, red.DecidedAt) = (b.Fulfilled ? "Fulfilled" : "Rejected", string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim(), DateTime.UtcNow);
        if (!b.Fulfilled)
        {
            _db.PointsLedger.Add(new PointsEntry { EmployeeId = red.EmployeeId, Delta = red.Cost, Reason = "Refund", RefId = red.Id, Note = red.RewardTitle });
            var r = await _db.Rewards.FirstOrDefaultAsync(x => x.Id == red.RewardId, ct);
            if (r?.Stock is { } st) r.Stock = st + 1;
        }
        await _db.SaveChangesAsync(ct);
        await _notify.InAppAsync(Tenant, red.EmployeeId,
            b.Fulfilled ? $"Ödülünüz hazır: {red.RewardTitle}" : $"Ödül talebiniz reddedildi: {red.RewardTitle}",
            b.Fulfilled ? (red.Note ?? "İK ödülünüzü teslim etti.") : $"{red.Note} {red.Cost} puan hesabınıza iade edildi.", "recognition.redemption", ct);
        return Ok(new { red.Id, red.Status });
    }

    public sealed record GrantInput(Guid EmployeeId, int Points, string Note);

    /// <summary>İK: bir çalışana puan ekler ya da düşer (ör. yarışma ödülü, düzeltme). Gerekçe zorunlu.</summary>
    [HttpPost("grant")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Grant(GrantInput b, CancellationToken ct)
    {
        if (b.Points == 0 || Math.Abs(b.Points) > 100000) return BadRequest(new { message = "Puan 0 olamaz, en fazla 100000" });
        var note = (b.Note ?? "").Trim();
        if (note.Length is < 3 or > 200) return BadRequest(new { message = "Gerekçe 3-200 karakter olmalı" });
        var p = await People.FindAsync(Tenant, b.EmployeeId, ct);
        if (p is null) return NotFound(new { message = "Çalışan bulunamadı" });
        _db.PointsLedger.Add(new PointsEntry { EmployeeId = p.Id, Delta = b.Points, Reason = "Grant", Note = note });
        await _db.SaveChangesAsync(ct);
        if (b.Points > 0)
            await _notify.InAppAsync(Tenant, p.Id, $"Hesabınıza {b.Points} takdir puanı eklendi", note, "recognition.grant", ct);
        return Ok(new { balance = await RecognitionPoints.BalanceAsync(_db, p.Id, ct) });
    }
}
