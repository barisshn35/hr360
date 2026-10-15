using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LearningService.Data;
using LearningService.Tenancy;

namespace LearningService.Services;

/// <summary>Çalışan + güncel görevlendirme (departman, unvan, departman başkanı).</summary>
public sealed class PersonRow
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? PositionTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? HeadId { get; set; }
    public string Status { get; set; } = "";
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public sealed class DepartmentRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? HeadEmployeeId { get; set; }
}

public static class LearningCaller
{
    public static bool IsHr(this ClaimsPrincipal u) =>
        u.IsInRole("hr-admin") || u.IsInRole("tenant-admin") || u.IsInRole("platform-admin")
        || u.IsInRole("ext-learning-manage");

    public static string UserId(this ClaimsPrincipal u) =>
        u.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";

    public static string DisplayName(this ClaimsPrincipal u) =>
        u.FindFirst("name")?.Value ?? u.FindFirst("preferred_username")?.Value ?? u.UserId();
}

/// <summary>
/// Diğer modüllerin tablolarını (çalışan, görevlendirme, departman) SALT OKUNUR sorgular —
/// tüm servisler aynı veritabanını paylaşır; her sorgu kiracı slug'ı ile filtrelenir.
/// Yetki kuralı (Y19/Y20 sonuçları): çalışanın kendisi, departman başkanı ve İK görür.
/// </summary>
public sealed class LearningDirectory
{
    private readonly LearningDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IHttpContextAccessor _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LearningDirectory(LearningDbContext db, ITenantContext tenant, IHttpContextAccessor http)
    {
        _db = db; _tenant = tenant; _http = http;
    }

    private string Tenant => _tenant.TenantSlug ?? "";

    private const string Base = """
        SELECT e."Id", e."FirstName", e."LastName", a."PositionTitle", a."DepartmentId",
               d."Name" AS "DepartmentName", d."HeadEmployeeId" AS "HeadId", e."Status"
        FROM employee_employees e
        LEFT JOIN LATERAL (
            SELECT x."PositionTitle", x."DepartmentId" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= current_date
              AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        WHERE e."TenantSlug" = {0}
        """;

    public async Task<PersonRow?> MeAsync(CancellationToken ct)
    {
        var ctx = _http.HttpContext;
        const string key = "__hr360_learning_me";
        if (ctx is not null && ctx.Items.TryGetValue(key, out var cached)) return cached as PersonRow;
        PersonRow? me = null;
        var uid = ctx?.User.UserId();
        if (!string.IsNullOrEmpty(Tenant) && uid is not null)
            me = await _db.Database.SqlQueryRaw<PersonRow>(Base + " AND e.\"KeycloakUserId\" = {1}", Tenant, uid)
                .FirstOrDefaultAsync(ct);
        if (ctx is not null) ctx.Items[key] = me;
        return me;
    }

    public Task<PersonRow?> FindAsync(Guid id, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<PersonRow>(Base + " AND e.\"Id\" = {1}", Tenant, id).FirstOrDefaultAsync(ct);

    public Task<List<PersonRow>> ActiveAsync(CancellationToken ct) =>
        _db.Database.SqlQueryRaw<PersonRow>(Base + " AND e.\"Status\" <> 'Terminated'", Tenant).ToListAsync(ct);

    public Task<List<DepartmentRow>> DepartmentsAsync(CancellationToken ct) =>
        _db.Database.SqlQueryRaw<DepartmentRow>(
            "SELECT \"Id\", \"Name\", \"HeadEmployeeId\" FROM organization_departments WHERE \"TenantSlug\" = {0} ORDER BY \"Name\"",
            Tenant).ToListAsync(ct);

    public Task<string?> TenantNameAsync(CancellationToken ct) =>
        _db.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM platform_tenants WHERE \"Slug\" = {0}", Tenant)
            .FirstOrDefaultAsync(ct);

    /// <summary>Çalışanın kendisi, departman başkanı ya da İK mı?</summary>
    public static bool CanSee(PersonRow target, Guid? me, bool isHr) =>
        isHr || (me is not null && (target.Id == me || target.HeadId == me));

    public async Task AuditAsync(string entityType, string entityId, string action, object changes)
    {
        var ctx = _http.HttpContext;
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'learning-service',{1},{2},{3},{4}::jsonb,{5},{6},{7},{8},now())",
                (object?)_tenant.TenantSlug ?? DBNull.Value, entityType, entityId, action, JsonSerializer.Serialize(changes, Json),
                (object?)ctx?.User.UserId() ?? DBNull.Value, (object?)ctx?.User.DisplayName() ?? DBNull.Value,
                (object?)(ctx?.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? ctx?.TraceIdentifier) ?? DBNull.Value,
                (object?)ctx?.Request.Headers["X-Real-IP"].FirstOrDefault() ?? DBNull.Value);
        }
        catch (Exception) { /* denetim yazılamazsa iş akışı bozulmaz */ }
    }

    public static async Task NotifyAsync(LearningDbContext db, string tenant, Guid employeeId, string subject, string body,
        string code, string? actionUrl, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","ActionUrl")
                VALUES ({0},{1},{2},NULL,'InApp',{3},{4},{5},'Pending',0,now(),{6})
                """, Guid.NewGuid(), tenant, employeeId, code, subject, body, (object?)actionUrl ?? DBNull.Value);
        }
        catch (Exception) { }
    }
}
