using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PerformanceService.Data;
using PerformanceService.Tenancy;

namespace PerformanceService.Services;

public sealed class PerfPersonRow
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? PositionTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? HeadId { get; set; }
    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>
/// G12 için çalışan/departman okuyucu (paylaşılan veritabanında SALT OKUNUR, kiracı filtreli) ve
/// denetim kaydı yazıcı. "Yönetici" = çalışanın güncel departmanının başkanı.
/// </summary>
public sealed class PerfPeople
{
    private readonly PerformanceDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IHttpContextAccessor _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PerfPeople(PerformanceDbContext db, ITenantContext tenant, IHttpContextAccessor http)
    {
        _db = db; _tenant = tenant; _http = http;
    }

    private string Tenant => _tenant.TenantSlug ?? "";

    private const string Base = """
        SELECT e."Id", e."FirstName", e."LastName", a."PositionTitle", a."DepartmentId",
               d."Name" AS "DepartmentName", d."HeadEmployeeId" AS "HeadId"
        FROM employee_employees e
        LEFT JOIN LATERAL (
            SELECT x."PositionTitle", x."DepartmentId" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= current_date
              AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        WHERE e."TenantSlug" = {0}
        """;

    public static string UserId(ClaimsPrincipal u) =>
        u.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";

    public static string DisplayName(ClaimsPrincipal u) =>
        u.FindFirst("name")?.Value ?? u.FindFirst("preferred_username")?.Value ?? UserId(u);

    public async Task<PerfPersonRow?> MeAsync(CancellationToken ct)
    {
        var ctx = _http.HttpContext;
        const string key = "__hr360_perf_me_sql";
        if (ctx is not null && ctx.Items.TryGetValue(key, out var cached)) return cached as PerfPersonRow;
        PerfPersonRow? me = null;
        if (!string.IsNullOrEmpty(Tenant) && ctx is not null)
            me = await _db.Database.SqlQueryRaw<PerfPersonRow>(Base + " AND e.\"KeycloakUserId\" = {1}", Tenant, UserId(ctx.User))
                .FirstOrDefaultAsync(ct);
        if (ctx is not null) ctx.Items[key] = me;
        return me;
    }

    public Task<PerfPersonRow?> FindAsync(Guid id, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<PerfPersonRow>(Base + " AND e.\"Id\" = {1}", Tenant, id).FirstOrDefaultAsync(ct);

    public Task<List<PerfPersonRow>> ActiveAsync(CancellationToken ct) =>
        _db.Database.SqlQueryRaw<PerfPersonRow>(Base + " AND e.\"Status\" <> 'Terminated'", Tenant).ToListAsync(ct);

    public async Task AuditAsync(string entityType, string entityId, string action, object changes)
    {
        var ctx = _http.HttpContext;
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'performance-service',{1},{2},{3},{4}::jsonb,{5},{6},{7},{8},now())",
                (object?)_tenant.TenantSlug ?? DBNull.Value, entityType, entityId, action, JsonSerializer.Serialize(changes, Json),
                ctx is null ? DBNull.Value : (object)UserId(ctx.User), ctx is null ? DBNull.Value : (object)DisplayName(ctx.User),
                (object?)(ctx?.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? ctx?.TraceIdentifier) ?? DBNull.Value,
                (object?)ctx?.Request.Headers["X-Real-IP"].FirstOrDefault() ?? DBNull.Value);
        }
        catch (Exception) { /* denetim yazılamazsa iş akışı bozulmaz */ }
    }
}
