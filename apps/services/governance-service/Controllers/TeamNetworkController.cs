using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Ekip ağı (3B): ekipler arası etkileşim yoğunluğu — takdir (kudos), 1:1
 * görüşme ve ortak hedef. Diğer modüllerin tabloları SALT OKUNUR sorgulanır
 * (organization, engagement, performance, employee); hiçbirine yazılmaz.
 * Yanıtta kişi yoktur, yalnızca ekip düğümleri ve ekip çiftleri; küçük ekip
 * ve küçük sayı gizlemesi TeamNetwork.Build'de. Görüntüleme audit_log'a
 * "SensitiveViewed" olarak yazılır (davranış verisinin toplu görünümü).
 * ==================================================================== */
[Route("api/team-network")]
[Authorize(Policy = "RequireHrAdmin")]
public class TeamNetworkController : AppController
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 90, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 30, 365);
        var since = DateTime.UtcNow.Date.AddDays(-days);

        // Etkin ekipler ve bugün üye olanlar (ayrılmamış ya da ayrılış tarihi bugün/ileride).
        const string members = """
            SELECT m."TeamId", m."EmployeeId" FROM organization_team_members m
            JOIN organization_teams t ON t."Id" = m."TeamId" AND t."IsActive"
            WHERE m."TenantSlug" = $1 AND (m."LeftOn" IS NULL OR m."LeftOn" >= current_date)
            """;

        var teams = await Db.QueryAsync($"""
            WITH tm AS ({members})
            SELECT t."Id", t."Name", d."Name", coalesce(array_agg(DISTINCT tm."EmployeeId") FILTER (WHERE tm."EmployeeId" IS NOT NULL), ARRAY[]::uuid[])
            FROM organization_teams t
            LEFT JOIN organization_departments d ON d."Id" = t."DepartmentId"
            LEFT JOIN tm ON tm."TeamId" = t."Id"
            WHERE t."TenantSlug" = $1 AND t."IsActive"
            GROUP BY t."Id", t."Name", d."Name"
            """, r => new TeamNetwork.TeamRow(r.GetGuid(0), r.GetString(1), r.Str(2), r.GetFieldValue<Guid[]>(3)), ct, Tenant);

        // Kişi çiftleri doğrudan ekip çiftlerine toplanır; kişi düzeyi veri sorgudan çıkmaz.
        //  - takdir: veren → alan
        //  - 1:1: yönetici (KeycloakUserId ile çalışan kaydı) ↔ çalışan; iptal edilenler ve ileri tarihliler hariç
        //  - ortak hedef: aynı dönemde aynı başlıklı hedef (ekip başına bir kez sayılır; ekip içinde ≥ 2 kişi)
        var pairs = await Db.QueryAsync($"""
            WITH tm AS ({members}),
            p AS (
                SELECT 'kudos' AS kind, k."FromEmployeeId" AS a, k."ToEmployeeId" AS b
                FROM engagement_kudos k
                WHERE k."TenantSlug" = $1 AND k."CreatedAt" >= $2 AND k."FromEmployeeId" IS NOT NULL
                UNION ALL
                SELECT 'oneOnOne', mgr."Id", o."EmployeeId"
                FROM engagement_one_on_ones o
                JOIN employee_employees mgr ON mgr."TenantSlug" = o."TenantSlug" AND mgr."KeycloakUserId" = o."ManagerUserId"
                WHERE o."TenantSlug" = $1 AND o."ScheduledAt" >= $2 AND o."ScheduledAt" <= now() AND o."Status" <> 'Cancelled'
            ),
            g AS (
                SELECT DISTINCT g."CycleId", lower(btrim(g."Title")) AS title, g."EmployeeId"
                FROM performance_goals g WHERE g."TenantSlug" = $1 AND g."CreatedAt" >= $2
            ),
            gt AS (
                SELECT g."CycleId", g.title, tm."TeamId", count(DISTINCT g."EmployeeId") AS n
                FROM g JOIN tm ON tm."EmployeeId" = g."EmployeeId" GROUP BY 1, 2, 3
            )
            SELECT least(ta."TeamId", tb."TeamId"), greatest(ta."TeamId", tb."TeamId"), p.kind, count(*)
            FROM p JOIN tm ta ON ta."EmployeeId" = p.a JOIN tm tb ON tb."EmployeeId" = p.b
            WHERE p.a <> p.b
            GROUP BY 1, 2, 3
            UNION ALL
            SELECT least(x."TeamId", y."TeamId"), greatest(x."TeamId", y."TeamId"), 'sharedGoal', count(*)
            FROM gt x JOIN gt y ON y."CycleId" = x."CycleId" AND y.title = x.title
              AND (x."TeamId" < y."TeamId" OR (x."TeamId" = y."TeamId" AND x.n >= 2))
            GROUP BY 1, 2
            """, r => new TeamNetwork.PairRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt64(3)), ct, Tenant, since);

        var result = TeamNetwork.Build(teams, pairs, L("Diğer", "Other"));

        await ComplianceAudit.WriteAsync(Db, Tenant, "TeamNetwork", "team-network", "SensitiveViewed",
            new { view = "teamNetwork", days, teams = result.Nodes.Count, edges = result.Edges.Count }, Me.UserId, Me.Name, ct);

        return Ok(new
        {
            days,
            since = DateOnly.FromDateTime(since),
            minTeamSize = TeamNetwork.MinTeamSize,
            minEdgeCount = TeamNetwork.MinEdgeCount,
            nodes = result.Nodes.Select(n => new
            {
                id = n.Id, name = n.Name, department = n.Department, members = n.Members, mergedTeams = n.MergedTeams,
                @internal = new { kudos = n.Internal.Kudos, oneOnOnes = n.Internal.OneOnOnes, sharedGoals = n.Internal.SharedGoals },
            }),
            edges = result.Edges.Select(e => new
            {
                source = e.Source, target = e.Target, weight = e.Weight,
                kudos = e.Counts.Kudos, oneOnOnes = e.Counts.OneOnOnes, sharedGoals = e.Counts.SharedGoals,
            }),
            hiddenTeams = result.HiddenTeams,
            hiddenEdges = result.HiddenEdges,
        });
    }
}
