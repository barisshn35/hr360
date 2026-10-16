using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Fikir kutusu (yenilik haritası #24). Çalışan fikir yazar (isteğe bağlı
 * anonim), herkes bir kez destekler ve yorumlar; İK durum ve yanıt verir,
 * fikir sahibine bildirim gider. Anonim fikirde yazar kimliği hiçbir
 * yanıtta dönmez ve veritabanında tutulmaz (yalnızca tek yönlü anahtar).
 * ==================================================================== */
[Route("api/ideas")]
[Authorize]
public class IdeasController : AppController
{
    public static readonly string[] Statuses = ["New", "UnderReview", "Planned", "Done", "Declined"];
    public static readonly string[] Categories = ["İş süreçleri", "Çalışma ortamı", "Müşteri", "Teknoloji", "Sosyal", "Tasarruf", "Diğer"];

    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public IdeasController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    /// <summary>Kişinin kiracı içindeki tek yönlü anahtarı (anonim fikrin sahibini ve desteği tanımak için).</summary>
    private string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Tenant}:{Me.UserId}:hr360-idea")));

    private object Dto(Idea i, string key, int commentCount) => new
    {
        i.Id, i.Title, i.Body, i.Category, i.Status, i.Response, i.RespondedByName, i.RespondedAt, i.CreatedAt, i.Anonymous,
        authorName = i.Anonymous ? null : i.AuthorName, authorEmployeeId = i.Anonymous ? null : i.AuthorEmployeeId,
        mine = i.AuthorKey == key, votes = i.Voters.Count, votedByMe = i.Voters.Contains(key), commentCount,
    };

    [HttpGet("meta")]
    public IActionResult Meta() => Ok(new { statuses = Statuses, categories = Categories });

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string sort = "top", CancellationToken ct = default)
    {
        var q = _db.Ideas.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(i => i.Status == status);
        var items = await q.ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var counts = await _db.IdeaComments.AsNoTracking().Where(c => ids.Contains(c.IdeaId)).GroupBy(c => c.IdeaId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var ordered = sort == "new" ? items.OrderByDescending(i => i.CreatedAt) : items.OrderByDescending(i => i.Voters.Count).ThenByDescending(i => i.CreatedAt);
        var key = Key;
        return Ok(ordered.Take(300).Select(i => Dto(i, key, counts.GetValueOrDefault(i.Id))));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var i = await _db.Ideas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        var comments = await _db.IdeaComments.AsNoTracking().Where(c => c.IdeaId == id).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return Ok(new
        {
            idea = Dto(i, Key, comments.Count),
            comments = comments.Select(c => new { c.Id, c.AuthorName, c.AuthorEmployeeId, c.Body, c.CreatedAt, mine = c.AuthorUserId == Me.UserId }),
        });
    }

    public sealed record IdeaInput(string Title, string Body, string Category, bool Anonymous);

    [HttpPost]
    public async Task<IActionResult> Create(IdeaInput body, CancellationToken ct)
    {
        var title = (body.Title ?? "").Trim();
        var text = (body.Body ?? "").Trim();
        if (title.Length is < 5 or > 120) return BadRequest(new { message = "Başlık 5-120 karakter olmalı" });
        if (text.Length is < 10 or > 2000) return BadRequest(new { message = "Açıklama 10-2000 karakter olmalı" });
        if (!Categories.Contains(body.Category)) return BadRequest(new { message = "Geçersiz kategori" });
        var me = await MyPersonAsync(ct);
        var idea = new Idea
        {
            AuthorKey = Key, Anonymous = body.Anonymous,
            AuthorEmployeeId = body.Anonymous ? null : me?.Id, AuthorName = body.Anonymous ? null : me?.Name ?? Me.Name,
            Title = title, Body = text, Category = body.Category,
        };
        _db.Ideas.Add(idea);
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(idea, Key, 0));
    }

    /// <summary>Desteği açar/kapatır (kişi başına bir destek; kendi fikrine de verilebilir).</summary>
    [HttpPost("{id:guid}/vote")]
    public async Task<IActionResult> Vote(Guid id, CancellationToken ct)
    {
        var i = await _db.Ideas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        var key = Key;
        i.Voters = i.Voters.Contains(key) ? i.Voters.Where(v => v != key).ToList() : [.. i.Voters, key];
        await _db.SaveChangesAsync(ct);
        return Ok(new { votes = i.Voters.Count, votedByMe = i.Voters.Contains(key) });
    }

    public sealed record CommentInput(string Body);

    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> Comment(Guid id, CommentInput body, CancellationToken ct)
    {
        var text = (body.Body ?? "").Trim();
        if (text.Length is < 1 or > 1000) return BadRequest(new { message = "Yorum 1-1000 karakter olmalı" });
        var i = await _db.Ideas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        var me = await MyPersonAsync(ct);
        var c = new IdeaComment { IdeaId = id, AuthorUserId = Me.UserId, AuthorEmployeeId = me?.Id, AuthorName = me?.Name ?? Me.Name, Body = text };
        _db.IdeaComments.Add(c);
        await _db.SaveChangesAsync(ct);
        // Yazar adı açık fikirde yazara bildirim (anonimde kim olduğu bilinmediği için bildirilemez).
        if (i.AuthorEmployeeId is { } author && author != me?.Id)
            await _notify.InAppAsync(Tenant, author, "Fikrinize yorum yapıldı", $"\"{i.Title}\": {c.AuthorName} yorum yaptı.", "ideas.comment", ct);
        return Ok(new { c.Id, c.AuthorName, c.Body, c.CreatedAt, mine = true });
    }

    [HttpDelete("comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid commentId, CancellationToken ct)
    {
        var c = await _db.IdeaComments.FirstOrDefaultAsync(x => x.Id == commentId, ct);
        if (c is null) return NotFound();
        if (c.AuthorUserId != Me.UserId && !Me.IsHr) return StatusCode(403, new { message = "Yalnızca kendi yorumunuzu silebilirsiniz" });
        _db.IdeaComments.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record StatusInput(string Status, string? Response);

    /// <summary>İK: durum ve yanıt. Yazar adı açıksa fikir sahibine bildirim gider.</summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SetStatus(Guid id, StatusInput body, CancellationToken ct)
    {
        if (!Statuses.Contains(body.Status)) return BadRequest(new { message = "Geçersiz durum" });
        var response = string.IsNullOrWhiteSpace(body.Response) ? null : body.Response.Trim();
        if (response is { Length: > 2000 }) return BadRequest(new { message = "Yanıt en fazla 2000 karakter olabilir" });
        if (body.Status == "Declined" && response is null) return BadRequest(new { message = "Uygun görülmeyen fikre kısa bir gerekçe yazın" });
        var i = await _db.Ideas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        var changed = i.Status != body.Status;
        i.Status = body.Status;
        i.Response = response;
        i.RespondedByName = Me.Name;
        i.RespondedAt = DateTime.UtcNow;
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (changed && i.AuthorEmployeeId is { } author)
        {
            var label = body.Status switch { "UnderReview" => "inceleniyor", "Planned" => "planlandı", "Done" => "uygulandı", "Declined" => "uygun görülmedi", _ => "yeni" };
            await _notify.InAppAsync(Tenant, author, "Fikrinizin durumu güncellendi", $"\"{i.Title}\" {label}.{(response is null ? "" : " " + response)}", "ideas.status", ct);
        }
        return Ok(Dto(i, Key, await _db.IdeaComments.CountAsync(c => c.IdeaId == id, ct)));
    }

    /// <summary>Fikri yazarı (henüz yanıtlanmadıysa) ya da İK siler.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var i = await _db.Ideas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        var mine = i.AuthorKey == Key;
        if (!Me.IsHr && !(mine && i.Status == "New")) return StatusCode(403, new { message = "Yanıtlanmış fikri yalnızca İK kaldırabilir" });
        _db.Ideas.Remove(i);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
