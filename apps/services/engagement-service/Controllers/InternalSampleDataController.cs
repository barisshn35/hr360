using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Models;
using EngagementService.Tenancy;

namespace EngagementService.Controllers;

/// <summary>
/// Örnek veri (yenilik haritası #35): örnek çalışanlar arasında takdirler, fikirler ve yaklaşan bir
/// etkinlik. YALNIZCA tenant-service çağırır (X-Internal-Token). Örnek içerik "sample" işaretiyle
/// yazılır (takdirde gönderen kullanıcı, fikirde yazar anahtarı, etkinlikte oluşturan) ve temizlik
/// yalnızca bu işaretli kayıtlarla verilen örnek çalışanların kayıtlarını siler. Bildirim gönderilmez.
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalSampleDataController : ControllerBase
{
    public const string Marker = "sample";
    private readonly EngagementDbContext _db;
    private readonly TenantContext _tenant;
    public InternalSampleDataController(EngagementDbContext db, TenantContext tenant) { _db = db; _tenant = tenant; }

    public record SamplePerson(Guid Id, string Name);
    public record SampleRequest(string TenantSlug, List<SamplePerson> People);
    public record RemoveRequest(string TenantSlug, List<Guid> EmployeeIds);

    private IActionResult? Enter(string? slug)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault())) return NotFound();
        if (string.IsNullOrWhiteSpace(slug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        _tenant.TenantSlug = slug.Trim();
        _tenant.IsPlatformAdmin = false;
        // Denetim kaydında yazan "örnek veri" olarak görünsün.
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Marker), new Claim("name", "Örnek veri")], "internal-sample"));
        return null;
    }

    private static readonly (string Badge, string Message)[] KudosTexts =
    [
        ("teamwork", "Sprint sonunda herkese yardım ettiğin için teşekkürler!"),
        ("customer", "Müşterinin sorununu aynı gün çözdün, harikaydın."),
        ("innovation", "Raporlama fikrin ekibe her hafta saatler kazandırıyor."),
        ("mentor", "Yeni gelenlere gösterdiğin sabır için teşekkürler."),
        ("extra-mile", "Hafta sonu yayına çıkışı sorunsuz yönettin."),
        ("thanks", "Sunum hazırlığındaki desteğin için çok teşekkürler."),
        ("teamwork", "Departmanlar arası projede köprü oldun."),
        ("customer", "Zor bir görüşmeyi çok iyi yönettin."),
    ];

    private static readonly (string Title, string Body, string Category, string Status)[] IdeaTexts =
    [
        ("Toplantısız cuma öğleden sonraları", "Haftada bir yarım gün odaklı çalışma zamanı verimliliği artırır.", "Çalışma ortamı", "UnderReview"),
        ("Yeni gelenlere buddy eşleştirmesi", "İlk ay için her yeni çalışana deneyimli bir çalışma arkadaşı atansın.", "Sosyal", "Planned"),
        ("Ortak ekranlarda gösterge paneli", "Satış ve destek göstergeleri ofis ekranlarında canlı görünsün.", "Teknoloji", "New"),
    ];

    [HttpPost("/api/internal/engagement/sample")]
    public async Task<IActionResult> Create([FromBody] SampleRequest body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } err) return err;
        var p = body.People ?? [];
        if (p.Count < 4) return BadRequest(new { message = "En az 4 örnek kişi gerekli" });
        if (await _db.Kudos.AnyAsync(k => k.FromUserId == Marker, ct)) return Ok(new { created = false });
        var now = DateTime.UtcNow;
        for (var i = 0; i < KudosTexts.Length; i++)
        {
            var from = p[i % p.Count];
            var to = p[(i * 3 + 1) % p.Count];
            if (to.Id == from.Id) to = p[(i + 1) % p.Count];
            _db.Kudos.Add(new Kudos
            {
                FromUserId = Marker, FromEmployeeId = from.Id, FromName = from.Name, ToEmployeeId = to.Id, ToName = to.Name,
                Badge = KudosTexts[i].Badge, Message = KudosTexts[i].Message, CreatedAt = now.AddDays(-i * 2 - 1),
            });
        }
        for (var i = 0; i < IdeaTexts.Length; i++)
        {
            var a = p[(i * 2 + 2) % p.Count];
            _db.Ideas.Add(new Idea
            {
                AuthorKey = $"{Marker}:{Guid.NewGuid():N}", AuthorEmployeeId = a.Id, AuthorName = a.Name, Title = IdeaTexts[i].Title, Body = IdeaTexts[i].Body,
                Category = IdeaTexts[i].Category, Status = IdeaTexts[i].Status, CreatedAt = now.AddDays(-10 + i), UpdatedAt = now.AddDays(-10 + i),
                Voters = p.Take(3 + i).Select(x => $"{Marker}:{x.Id:N}").ToList(),
            });
        }
        var day = now.Date.AddDays(10).AddHours(6); // 10 gün sonra, 09:00 (TR)
        var ev = new CompanyEvent
        {
            Title = "Çeyrek sonu kahvaltısı", Description = "Tüm ekiplerle tanışma ve çeyrek değerlendirmesi.", Kind = "Social",
            StartsAt = day, EndsAt = day.AddHours(2), Location = "Ofis terası", Capacity = 40, CreatedByUserId = Marker, CreatedByName = "Örnek veri",
        };
        _db.Events.Add(ev);
        await _db.SaveChangesAsync(ct); // katılım satırları etkinliğe yabancı anahtarla bağlı: önce etkinlik
        foreach (var x in p.Take(Math.Min(6, p.Count)))
            _db.EventRsvps.Add(new EventRsvp { EventId = ev.Id, EmployeeId = x.Id, Name = x.Name, Status = "Going" });
        await _db.SaveChangesAsync(ct);
        return Ok(new { created = true });
    }

    [HttpPost("/api/internal/engagement/sample/remove")]
    public async Task<IActionResult> Remove([FromBody] RemoveRequest body, CancellationToken ct)
    {
        if (Enter(body.TenantSlug) is { } err) return err;
        var ids = body.EmployeeIds ?? [];
        _db.Kudos.RemoveRange(await _db.Kudos.Where(k => k.FromUserId == Marker || ids.Contains(k.ToEmployeeId) || (k.FromEmployeeId != null && ids.Contains(k.FromEmployeeId.Value))).ToListAsync(ct));
        _db.Ideas.RemoveRange(await _db.Ideas.Where(i => i.AuthorKey.StartsWith(Marker + ":") || (i.AuthorEmployeeId != null && ids.Contains(i.AuthorEmployeeId.Value))).ToListAsync(ct));
        _db.Events.RemoveRange(await _db.Events.Where(e => e.CreatedByUserId == Marker).ToListAsync(ct));
        _db.EventRsvps.RemoveRange(await _db.EventRsvps.Where(r => ids.Contains(r.EmployeeId)).ToListAsync(ct));
        _db.CardSignatures.RemoveRange(await _db.CardSignatures.Where(s => ids.Contains(s.EmployeeId)).ToListAsync(ct));
        _db.Cards.RemoveRange(await _db.Cards.Where(c => ids.Contains(c.RecipientEmployeeId)).ToListAsync(ct));
        _db.CoffeeParticipants.RemoveRange(await _db.CoffeeParticipants.Where(c => ids.Contains(c.EmployeeId)).ToListAsync(ct));
        _db.PointsLedger.RemoveRange(await _db.PointsLedger.Where(x => ids.Contains(x.EmployeeId)).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        if (ids.Count > 0)
            await HttpContext.RequestServices.GetRequiredService<EngagementService.Infrastructure.Sql>().ExecuteAsync(
                """DELETE FROM engagement_badge_awards WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2)""", ct, _tenant.TenantSlug!, ids.ToArray());
        return Ok(new { removed = true });
    }
}
