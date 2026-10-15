using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;
using RecruitmentService.Models;
using RecruitmentService.Services;

namespace RecruitmentService.Controllers;

[ApiController]
[Route("api/candidates")]
[Authorize(Policy = "RequireManagerOrAbove")]
public class CandidatesController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    public CandidatesController(RecruitmentDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? search)
    {
        // Liste "Başvuru" sütunu için başvurular da gelir (yalnızca başvuru satırları; mülakatlar değil).
        var q = _db.Candidates.AsNoTracking().Include(c => c.Applications).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(c =>
                c.FirstName.ToLower().Contains(term) ||
                c.LastName.ToLower().Contains(term) ||
                c.Email.ToLower().Contains(term));
        }
        return Ok(await q.OrderByDescending(c => c.CreatedAt).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var c = await _db.Candidates
            .Include(x => x.Applications).ThenInclude(a => a.Interviews)
            .FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? NotFound() : Ok(c);
    }

    /// <summary>
    /// G13: elle aday eklerken tekrar tespiti. Aynı (normalize) e-posta her zaman 409'dur (tekil kayıt);
    /// aynı telefon (benzer ya da farklı adla) 409 + mevcut aday kimliği döner, İK "Force" ile yine de
    /// kaydedebilir.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCandidateRequest request, CancellationToken ct)
    {
        var first = request.FirstName?.Trim() ?? "";
        var last = request.LastName?.Trim() ?? "";
        var email = request.Email?.Trim() ?? "";
        if (first.Length is < 1 or > 100 || last.Length is < 1 or > 100) return BadRequest(new { message = "Ad ve soyad zorunlu (en fazla 100 karakter)" });
        if (email.Length is < 3 or > 200 || !email.Contains('@')) return BadRequest(new { message = "Geçerli bir e-posta adresi girin" });
        if (request.ResumeText is { Length: > 20000 }) return BadRequest(new { message = "Özgeçmiş metni en fazla 20.000 karakter olabilir" });

        var match = await FindDuplicateAsync(first, last, email, request.Phone, ct);
        if (match is not null && (match.Reason == "email" || !request.Force))
            return Conflict(new
            {
                message = DuplicateDetector.Describe(match.Reason),
                code = "duplicate_candidate",
                existingCandidateId = match.CandidateId,
                reason = match.Reason,
                canForce = match.Reason != "email",
            });

        var candidate = new Candidate
        {
            FirstName = first,
            LastName = last,
            Email = email,
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            ResumeStorageKey = request.ResumeStorageKey,
            Source = request.Source,
            ResumeText = string.IsNullOrWhiteSpace(request.ResumeText) ? null : request.ResumeText.Trim(),
            Skills = (request.Skills ?? new()).Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList(),
        };
        candidate.Normalize();
        _db.Candidates.Add(candidate);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "Bu e-posta ile kayıtlı aday zaten var", code = "duplicate_candidate", reason = "email", canForce = false });
        }
        return CreatedAtAction(nameof(GetById), new { id = candidate.Id }, candidate);
    }

    /// <summary>Form doldurulurken canlı tekrar uyarısı için (kayıt yapmaz).</summary>
    [HttpGet("duplicates")]
    public async Task<IActionResult> CheckDuplicates([FromQuery] string? firstName, [FromQuery] string? lastName,
        [FromQuery] string? email, [FromQuery] string? phone, CancellationToken ct)
    {
        var match = await FindDuplicateAsync(firstName ?? "", lastName ?? "", email, phone, ct);
        if (match is null) return Ok(new { duplicate = false });
        var c = await _db.Candidates.AsNoTracking().Where(x => x.Id == match.CandidateId)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.CreatedAt }).FirstAsync(ct);
        return Ok(new
        {
            duplicate = true, strength = match.Strength.ToString(), reason = match.Reason,
            message = DuplicateDetector.Describe(match.Reason), candidate = c, canForce = match.Reason != "email",
        });
    }

    private async Task<DuplicateDetector.Match?> FindDuplicateAsync(string first, string last, string? email, string? phone, CancellationToken ct)
    {
        var ne = DuplicateDetector.NormalizeEmail(email);
        var np = DuplicateDetector.NormalizePhone(phone);
        if (ne is null && np is null) return null;
        var pool = await _db.Candidates.AsNoTracking()
            .Where(c => c.AnonymizedAt == null && ((ne != null && c.NormalizedEmail == ne) || (np != null && c.NormalizedPhone == np)))
            .Select(c => new DuplicateDetector.Candidate(c.Id, c.FirstName, c.LastName, c.NormalizedEmail, c.NormalizedPhone))
            .ToListAsync(ct);
        return DuplicateDetector.FindBest(pool, first, last, email, phone);
    }
}

public record CreateCandidateRequest(
    string FirstName, string LastName, string Email,
    string? Phone, string? ResumeStorageKey, string? Source,
    List<string>? Skills = null, string? ResumeText = null, bool Force = false);
