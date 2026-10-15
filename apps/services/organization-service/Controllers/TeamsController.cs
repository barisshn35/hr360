using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrganizationService.Data;
using OrganizationService.Infrastructure;
using OrganizationService.Models;

namespace OrganizationService.Controllers;

/// <summary>
/// Ekip yonetimi.
///
/// Yetki tercihi: ekip olusturma ve uye atama YONETICI yetkisi
/// (RequireManagerOrAbove). Onceki surumde organizasyon yapisini yalnizca
/// IK kurabiliyordu; pratikte ekipleri taniyan ve kuran kisi yoneticidir.
/// </summary>
[ApiController]
[Route("api/teams")]
[Authorize]
public class TeamsController : ControllerBase
{
    private readonly OrganizationDbContext _db;
    public TeamsController(OrganizationDbContext db) => _db = db;

    /// <remarks>
    /// Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi; sayı <c>X-Total-Count</c>
    /// başlığında). <c>page</c>/<c>pageSize</c> (en fazla 200) ile <c>{ items, total, page, pageSize }</c>.
    /// <c>q</c>: ekip adı. Üye listesi çalışan kimliği içerdiği için ekipler önbelleğe ALINMAZ.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? departmentId,
        [FromQuery] Guid? leadEmployeeId,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool includeMembers = false,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? q = null,
        [FromQuery] string? dir = null)
    {
        var query = _db.Teams.Include(t => t.Members).AsQueryable();
        if (!includeInactive) query = query.Where(t => t.IsActive);
        if (departmentId.HasValue) query = query.Where(t => t.DepartmentId == departmentId.Value);
        if (leadEmployeeId.HasValue) query = query.Where(t => t.LeadEmployeeId == leadEmployeeId.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = Paging.FoldLike(q);
            query = query.Where(t => EF.Functions.Like(OrganizationDbContext.Fold(t.Name), like, "\\"));
        }

        var ordered = (Paging.Desc(dir) ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name)).ThenBy(t => t.Id);
        int total;
        List<Team> teams;
        var (p, size) = Paging.Normalize(page ?? 1, pageSize);
        if (page is null)
        {
            teams = await ordered.ToListAsync();
            total = teams.Count;
        }
        else
        {
            total = await ordered.CountAsync();
            teams = total == 0 ? new List<Team>() : await ordered.Skip((p - 1) * size).Take(size).ToListAsync();
        }
        Paging.SetTotal(this, total);

        // includeMembers: organizasyon semasi cizen ekranlar her ekibin
        // ayrintisini ayri ayri cekmek zorunda kalmasin. 5 ekipte fark
        // etmez, 100 ekipte 100 istek eder.
        var shaped = teams.Select(t => new
        {
            t.Id, t.Name, t.Description, t.DepartmentId, t.LeadEmployeeId,
            t.IsActive, t.CreatedAt,
            memberCount = t.Members.Count(m => m.IsCurrent),
            members = includeMembers
                ? t.Members.Where(m => m.IsCurrent)
                    .OrderBy(m => m.JoinedOn)
                    .Select(m => new
                    {
                        m.Id, m.EmployeeId, m.RoleInTeam, m.JoinedOn,
                        isLead = t.LeadEmployeeId == m.EmployeeId,
                    })
                : null,
        }).ToList();
        return page is null ? Ok(shaped) : Ok(new { items = shaped, total, page = p, pageSize = size });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, [FromQuery] bool includeFormer = false)
    {
        var team = await _db.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();

        var members = includeFormer ? team.Members : team.Members.Where(m => m.IsCurrent).ToList();

        return Ok(new
        {
            team.Id, team.Name, team.Description, team.DepartmentId,
            team.LeadEmployeeId, team.IsActive, team.CreatedAt,
            members = members.OrderBy(m => m.JoinedOn).Select(m => new
            {
                m.Id, m.EmployeeId, m.RoleInTeam, m.JoinedOn, m.LeftOn, m.IsCurrent,
                isLead = team.LeadEmployeeId == m.EmployeeId,
            }),
        });
    }

    /// <summary>Bir calisanin uyesi oldugu ekipler.</summary>
    [HttpGet("by-employee/{employeeId}")]
    public async Task<IActionResult> ByEmployee(Guid employeeId, [FromQuery] bool includeFormer = false)
    {
        var q = _db.TeamMembers.Include(m => m.Team).Where(m => m.EmployeeId == employeeId);
        if (!includeFormer) q = q.Where(m => m.LeftOn == null);

        var memberships = await q.ToListAsync();

        return Ok(memberships.Select(m => new
        {
            teamId = m.TeamId,
            teamName = m.Team?.Name,
            departmentId = m.Team?.DepartmentId,
            m.RoleInTeam,
            m.JoinedOn,
            m.LeftOn,
            isLead = m.Team?.LeadEmployeeId == employeeId,
        }));
    }

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        if (!await _db.Departments.AnyAsync(d => d.Id == request.DepartmentId))
            return BadRequest(new { message = "Departman bulunamadi" });

        if (await _db.Teams.AnyAsync(t => t.Name == request.Name && t.DepartmentId == request.DepartmentId && t.IsActive))
            return Conflict(new { message = "Bu departmanda ayni isimde aktif ekip var" });

        var team = new Team
        {
            Name = request.Name,
            Description = request.Description,
            DepartmentId = request.DepartmentId,
            LeadEmployeeId = request.LeadEmployeeId,
        };

        _db.Teams.Add(team);

        // Lider otomatik olarak ekip uyesi yapilir - listede gorunmeyen
        // bir lider kafa karistirici olurdu.
        if (request.LeadEmployeeId.HasValue)
        {
            _db.TeamMembers.Add(new TeamMember
            {
                TeamId = team.Id,
                EmployeeId = request.LeadEmployeeId.Value,
                RoleInTeam = "Takım lideri",
                JoinedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });
        }

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = team.Id }, team);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTeamRequest request)
    {
        var team = await _db.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();

        team.Name = request.Name ?? team.Name;
        team.Description = request.Description ?? team.Description;
        if (request.IsActive.HasValue) team.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return Ok(team);
    }

    /// <summary>
    /// Takim liderini belirler ya da kaldirir (leadEmployeeId=null).
    /// Yeni lider ekipte degilse otomatik uye yapilir.
    /// </summary>
    [HttpPost("{id}/lead")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> SetLead(Guid id, [FromBody] SetLeadRequest request)
    {
        var team = await _db.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();

        if (request.LeadEmployeeId.HasValue)
        {
            var isMember = team.Members.Any(m => m.EmployeeId == request.LeadEmployeeId.Value && m.IsCurrent);
            if (!isMember)
            {
                _db.TeamMembers.Add(new TeamMember
                {
                    TeamId = team.Id,
                    EmployeeId = request.LeadEmployeeId.Value,
                    RoleInTeam = "Takım lideri",
                    JoinedOn = DateOnly.FromDateTime(DateTime.UtcNow),
                });
            }
        }

        team.LeadEmployeeId = request.LeadEmployeeId;
        await _db.SaveChangesAsync();

        return Ok(new { team.Id, team.Name, team.LeadEmployeeId });
    }

    [HttpPost("{id}/members")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberRequest request)
    {
        var team = await _db.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();

        if (team.Members.Any(m => m.EmployeeId == request.EmployeeId && m.IsCurrent))
            return Conflict(new { message = "Calisan zaten bu ekibin uyesi" });

        var member = new TeamMember
        {
            TeamId = id,
            EmployeeId = request.EmployeeId,
            RoleInTeam = request.RoleInTeam,
            JoinedOn = request.JoinedOn ?? DateOnly.FromDateTime(DateTime.UtcNow),
        };

        _db.TeamMembers.Add(member);
        await _db.SaveChangesAsync();
        return Created($"/api/teams/{id}", member);
    }

    /// <summary>
    /// Uyeyi ekipten cikarir. Kaydi SILMEZ, ayrilma tarihi yazar -
    /// gecmis donem raporlarinda "o donemde bu ekipteydi" bilgisi korunur.
    /// </summary>
    [HttpPost("{id}/members/{memberId}/remove")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> RemoveMember(
        Guid id, Guid memberId, [FromBody] RemoveMemberRequest? request)
    {
        var member = await _db.TeamMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.TeamId == id);
        if (member is null) return NotFound();
        if (member.LeftOn is not null)
            return BadRequest(new { message = "Uye zaten ayrilmis" });

        member.LeftOn = request?.LeftOn ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // Ayrilan kisi lider ise liderlik bosa dusurulur.
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == id);
        if (team?.LeadEmployeeId == member.EmployeeId) team.LeadEmployeeId = null;

        await _db.SaveChangesAsync();
        return Ok(member);
    }
}

public record CreateTeamRequest(string Name, Guid DepartmentId, string? Description, Guid? LeadEmployeeId);
public record UpdateTeamRequest(string? Name, string? Description, bool? IsActive);
public record SetLeadRequest(Guid? LeadEmployeeId);
public record AddMemberRequest(Guid EmployeeId, string? RoleInTeam, DateOnly? JoinedOn);
public record RemoveMemberRequest(DateOnly? LeftOn);
