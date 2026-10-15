using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;

namespace RecruitmentService.Controllers;

[ApiController]
[Route("api/job-postings")]
[Authorize]
public class JobPostingsController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    private readonly IAuthorizationService _authz;
    public JobPostingsController(RecruitmentDbContext db, IAuthorizationService authz)
    {
        _db = db;
        _authz = authz;
    }

    /// <summary>Aday verisini gorebilenler - CandidatesController ile ayni politika.</summary>
    private async Task<bool> CanSeeCandidatesAsync() =>
        (await _authz.AuthorizeAsync(User, "RequireManagerOrAbove")).Succeeded;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] JobPostingStatus? status)
    {
        var q = _db.JobPostings.AsQueryable();
        // Taslak (yayinlanmamis, gizli olabilecek) ilanlar yalnizca yetkililere.
        var canSee = await CanSeeCandidatesAsync();
        if (!canSee) q = q.Where(p => p.Status != JobPostingStatus.Draft);
        if (status.HasValue) q = q.Where(p => p.Status == status.Value);
        var list = await q.OrderByDescending(p => p.CreatedAt).ToListAsync();
        if (canSee && list.Count > 0)
        {
            // Başvuru sayısı tek gruplanmış sorguyla (başvurular listeye yüklenmez).
            var ids = list.Select(p => p.Id).ToList();
            var counts = await _db.Applications.Where(a => ids.Contains(a.JobPostingId))
                .GroupBy(a => a.JobPostingId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            foreach (var p in list) p.ApplicationCount = counts.GetValueOrDefault(p.Id);
        }
        return Ok(list);
    }

    /// <summary>
    /// GUVENLIK: Ilan detayi basvurulari ve adaylari (ad, e-posta, telefon, CV anahtari,
    /// basvuru notlari) de iceriyordu ve yalnizca [Authorize] ile korunuyordu - her
    /// calisan, aday listesinin yonetici+ kisitini bu uctan atlayarak aday kisisel
    /// verilerini okuyabiliyordu (canli dogrulandi). Yetkisizlere basvurusuz ilan doner.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (await CanSeeCandidatesAsync())
        {
            var full = await _db.JobPostings
                .Include(x => x.Applications).ThenInclude(a => a.Candidate)
                .FirstOrDefaultAsync(x => x.Id == id);
            return full is null ? NotFound() : Ok(full);
        }
        var p = await _db.JobPostings
            .FirstOrDefaultAsync(x => x.Id == id && x.Status != JobPostingStatus.Draft);
        if (p is null) return NotFound();
        p.Applications = new();
        return Ok(p);
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateJobPostingRequest request)
    {
        var posting = new JobPosting
        {
            Title = request.Title,
            DepartmentId = request.DepartmentId,
            Description = request.Description,
            EmploymentType = request.EmploymentType,
            Headcount = request.Headcount <= 0 ? 1 : request.Headcount
        };
        _db.JobPostings.Add(posting);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = posting.Id }, posting);
    }

    [HttpPost("{id}/publish")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Publish(Guid id)
    {
        var p = await _db.JobPostings.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Status == JobPostingStatus.Closed) return BadRequest("Kapali ilan yayinlanamaz");

        p.Status = JobPostingStatus.Published;
        p.PublishedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(p);
    }

    [HttpPost("{id}/close")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Close(Guid id)
    {
        var p = await _db.JobPostings.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();

        p.Status = JobPostingStatus.Closed;
        p.ClosedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(p);
    }
}

public record CreateJobPostingRequest(
    string Title, Guid DepartmentId, string? Description,
    EmploymentType EmploymentType, int Headcount);
