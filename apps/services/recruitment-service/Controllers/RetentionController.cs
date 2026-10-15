using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecruitmentService.Data;
using RecruitmentService.Services;

namespace RecruitmentService.Controllers;

/// <summary>Y16 / KVKK: aday verisi saklama ayarları ve elle imha turu (kiracının kendi verisi).</summary>
[ApiController]
[Route("api/retention")]
[Authorize(Policy = "RequireHrAdmin")]
public class RetentionController : ControllerBase
{
    private readonly RecruitmentDbContext _db;
    public RetentionController(RecruitmentDbContext db) => _db = db;

    [HttpGet("settings")]
    public IActionResult Settings() => Ok(new
    {
        retentionDays = RetentionService.RetentionDays,
        poolMonths = RetentionService.PoolMonths,
        noticeVersion = PublicCareerController.NoticeVersion,
    });

    /// <summary>Saklama süresi dolan adayları şimdi anonimleştirir (periyodik işçiyle aynı kural).</summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var done = await RetentionService.RunAsync(_db, "Manual", RecruitmentSql.UserId(User) ?? "hr", ct);
        return Ok(new { anonymized = done.Values.Sum() });
    }
}
