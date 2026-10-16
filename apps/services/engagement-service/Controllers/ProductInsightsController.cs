using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EngagementService.Infrastructure;

namespace EngagementService.Controllers;

/* ======================================================================
 * Ürün içi geri bildirim ve ekran kullanım ölçümü (yenilik haritası #33).
 * Kişi kimliği tutulmaz: geri bildirim isimsiz, kullanım gün/ekran
 * sayacıdır. Yazımlar SQL ile (denetim kaydına kullanıcı düşmesin).
 * ==================================================================== */
[Route("api/product")]
[Authorize]
public class ProductInsightsController : AppController
{
    /// <summary>
    /// Yalnızca panel ekran şablonları: harf ve tireden oluşan parçalar, en fazla 4 haneli sayı (ör. "360")
    /// ve ":id". Kimlik (GUID) içeren adres kabul edilmez: kayda/kişiye bağlanmasın.
    /// </summary>
    private static readonly Regex PathRule = new(@"^/panel(/[a-z\-]+|/[0-9]{1,4}|/:id){0,6}$", RegexOptions.Compiled);

    private string RoleGroup => Me.IsHr ? "hr" : Me.IsManager ? "manager" : "employee";

    public sealed record FeedbackInput(string Path, int Rating, string? Comment);

    [HttpPost("feedback")]
    public async Task<IActionResult> Feedback(FeedbackInput body, CancellationToken ct)
    {
        if (!PathRule.IsMatch(body.Path ?? "")) return BadRequest(new { message = "Geçersiz ekran" });
        if (body.Rating is not (1 or -1)) return BadRequest(new { message = "Geçersiz oy" });
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        if (comment is { Length: > 1000 }) return BadRequest(new { message = "Yorum en fazla 1000 karakter olabilir" });
        await Db.ExecuteAsync("""
            INSERT INTO engagement_product_feedback ("Id","TenantSlug","Path","Rating","Comment","RoleGroup","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,now())
            """, ct, Guid.NewGuid(), Tenant, body.Path, (short)body.Rating, comment, RoleGroup);
        return Ok(new { ok = true });
    }

    public sealed record ViewsInput(List<string> Paths);

    /// <summary>Ekran açılışları (istemci toplu gönderir, en fazla 100).</summary>
    [HttpPost("views")]
    public async Task<IActionResult> Views(ViewsInput body, CancellationToken ct)
    {
        var paths = (body.Paths ?? []).Take(100).Where(p => PathRule.IsMatch(p ?? "")).GroupBy(p => p).ToList();
        foreach (var g in paths)
            await Db.ExecuteAsync("""
                INSERT INTO engagement_page_views ("TenantSlug","Day","Path","Views") VALUES ($1, current_date, $2, $3)
                ON CONFLICT ("TenantSlug","Day","Path") DO UPDATE SET "Views" = engagement_page_views."Views" + EXCLUDED."Views"
                """, ct, Tenant, g.Key, g.Count());
        return NoContent();
    }

    /// <summary>İK: son N günde ekran başına açılma, olumlu/olumsuz oy ve yorumlar.</summary>
    [HttpGet("insights")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Insights([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var views = await Db.QueryAsync("""
            SELECT "Path", sum("Views")::int FROM engagement_page_views
            WHERE "TenantSlug" = $1 AND "Day" >= current_date - $2::int GROUP BY "Path"
            """, r => (Path: r.GetString(0), Views: r.GetInt32(1)), ct, Tenant, days);
        var votes = await Db.QueryAsync("""
            SELECT "Path", count(*) FILTER (WHERE "Rating" > 0)::int, count(*) FILTER (WHERE "Rating" < 0)::int FROM engagement_product_feedback
            WHERE "TenantSlug" = $1 AND "CreatedAt" >= now() - make_interval(days => $2::int) GROUP BY "Path"
            """, r => (Path: r.GetString(0), Up: r.GetInt32(1), Down: r.GetInt32(2)), ct, Tenant, days);
        var comments = await Db.QueryAsync("""
            SELECT "Path", "Rating", "Comment", "RoleGroup", "CreatedAt" FROM engagement_product_feedback
            WHERE "TenantSlug" = $1 AND "Comment" IS NOT NULL AND "CreatedAt" >= now() - make_interval(days => $2::int)
            ORDER BY "CreatedAt" DESC LIMIT 200
            """, r => new { path = r.GetString(0), rating = (int)r.GetInt16(1), comment = r.GetString(2), roleGroup = r.GetString(3), createdAt = r.GetFieldValue<DateTime>(4) }, ct, Tenant, days);
        var paths = views.Select(v => v.Path).Union(votes.Select(v => v.Path)).ToList();
        return Ok(new
        {
            days,
            screens = paths.Select(p => new
            {
                path = p,
                views = views.FirstOrDefault(v => v.Path == p).Views,
                up = votes.FirstOrDefault(v => v.Path == p).Up,
                down = votes.FirstOrDefault(v => v.Path == p).Down,
            }).OrderByDescending(s => s.views),
            comments,
        });
    }
}
