using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Npgsql;
using EngagementService.Tenancy;

namespace EngagementService.Infrastructure;

/// <summary>Oturumdaki kullanıcının JWT'den okunan özeti.</summary>
public sealed record UserInfo(string UserId, string Name, string? Email, IReadOnlySet<string> Roles)
{
    public bool IsHr => Roles.Overlaps(new[] { "hr-admin", "tenant-admin", "platform-admin", "ext-engagement-manage" });
    public bool IsManager => IsHr || Roles.Contains("manager");
    public bool IsPlatformAdmin => Roles.Contains("platform-admin");
}

public static class UserExtensions
{
    public static UserInfo Info(this ClaimsPrincipal user)
    {
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? "";
        var name = user.FindFirst("name")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Name)?.Value
            ?? "Kullanıcı";
        var email = user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value;
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet();
        return new UserInfo(id, name, email, roles);
    }
}

/// <summary>
/// Diğer modüllerin tablolarını (çalışan, departman, ilan…) SALT OKUNUR
/// sorgulamak için ince bir Npgsql yardımcısı. Tüm servisler aynı
/// "hr360_operational" veritabanını paylaştığı için çapraz-modül okumalar
/// HTTP turu yerine doğrudan SQL ile yapılır; YAZMA her zaman sahibi olan
/// servisin API'si üzerinden olur. Her sorgu kiracı slug'ı ile filtrelenir.
/// </summary>
public sealed class Sql
{
    private readonly NpgsqlDataSource _ds;
    public Sql(NpgsqlDataSource ds) => _ds = ds;

    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<object?> ScalarAsync(string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }
}

public static class ReaderExtensions
{
    public static string? Str(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    public static Guid? GuidOrNull(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    public static decimal? Dec(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));
    public static DateOnly? Date(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
    public static DateTime? Ts(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTime>(i);
}

/// <summary>Çalışan dizini — employee/organization tablolarından güncel görünüm.</summary>
public sealed record Person(
    Guid Id, string Name, string? Email, string? Position, Guid? DepartmentId, string? Department,
    DateOnly HireDate, string Status, string? UserId, Guid? DepartmentHeadId);

public sealed class PeopleDirectory
{
    private readonly Sql _sql;
    private readonly AppCache? _cache;
    public PeopleDirectory(Sql sql, AppCache? cache = null) { _sql = sql; _cache = cache; }

    private const string Base = """
        SELECT e."Id", e."FirstName" || ' ' || e."LastName", e."Email", a."PositionTitle", a."DepartmentId",
               d."Name", e."HireDate", e."Status", e."KeycloakUserId", d."HeadEmployeeId"
        FROM employee_employees e
        LEFT JOIN LATERAL (
            SELECT x."PositionTitle", x."DepartmentId" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= current_date
              AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        WHERE e."TenantSlug" = $1
        """;

    private static Person Map(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.Str(2), r.Str(3), r.GuidOrNull(4), r.Str(5),
        r.GetFieldValue<DateOnly>(6), r.GetString(7), r.Str(8), r.GuidOrNull(9));

    /// <summary>
    /// Kiracının çalışan listesi. Neredeyse her ekran bunu çağırdığı için 30 sn
    /// önbellekte tutulur (Redis varsa); ad/departman değişikliği en geç 30 sn'de yansır.
    /// </summary>
    public Task<List<Person>> ListAsync(string tenant, CancellationToken ct, bool includeTerminated = false)
    {
        Task<List<Person>> Load(CancellationToken c) =>
            _sql.QueryAsync(Base + (includeTerminated ? "" : " AND e.\"Status\" <> 'Terminated'") + " ORDER BY 2", Map, c, tenant);
        return _cache is null
            ? Load(ct)
            : _cache.GetOrSetAsync("people", tenant, includeTerminated ? "all" : "active", TimeSpan.FromSeconds(30), Load, ct);
    }

    /// <summary>
    /// Önbelleksiz liste: yetki kapsamı gibi anlık doğru olması gereken kararlar için (yeni işe giren
    /// ya da departmanı değişen kişi, önbellek süresi dolmadan yöneticisinin kapsamına girmeli).
    /// </summary>
    public Task<List<Person>> ListFreshAsync(string tenant, CancellationToken ct) =>
        _sql.QueryAsync(Base + " AND e.\"Status\" <> 'Terminated' ORDER BY 2", Map, ct, tenant);

    public async Task<Person?> FindAsync(string tenant, Guid id, CancellationToken ct)
        => (await _sql.QueryAsync(Base + " AND e.\"Id\" = $2", Map, ct, tenant, id)).FirstOrDefault();

    public async Task<Person?> FindByUserAsync(string tenant, string userId, CancellationToken ct)
        => (await _sql.QueryAsync(Base + " AND e.\"KeycloakUserId\" = $2", Map, ct, tenant, userId)).FirstOrDefault();

    /// <summary>
    /// Bir yöneticinin ekibi: başı olduğu departmanların çalışanları + lideri olduğu
    /// ekiplerin üyeleri (kendisi hariç).
    /// </summary>
    public async Task<List<Person>> TeamOfAsync(string tenant, Guid managerEmployeeId, CancellationToken ct)
    {
        // Yetki kapsamı: önbellekten değil, güncel veriden.
        var all = await ListFreshAsync(tenant, ct);
        var teamMemberIds = (await _sql.QueryAsync(
            """
            SELECT m."EmployeeId" FROM organization_team_members m
            JOIN organization_teams t ON t."Id" = m."TeamId"
            WHERE t."TenantSlug" = $1 AND t."LeadEmployeeId" = $2 AND m."LeftOn" IS NULL
            """, r => r.GetGuid(0), ct, tenant, managerEmployeeId)).ToHashSet();
        return all.Where(p => p.Id != managerEmployeeId &&
                              (p.DepartmentHeadId == managerEmployeeId || teamMemberIds.Contains(p.Id))).ToList();
    }
}

/// <summary>Uygulama içi bildirim (notification-service'in gelen kutusuna düşer).</summary>
public sealed class Notifier
{
    private readonly Sql _sql;
    private readonly ILogger<Notifier> _log;
    public Notifier(Sql sql, ILogger<Notifier> log) { _sql = sql; _log = log; }

    public async Task InAppAsync(string tenant, Guid recipientEmployeeId, string subject, string body, string code, CancellationToken ct)
    {
        try
        {
            await _sql.ExecuteAsync(
                """
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ($1,$2,$3,NULL,'InApp',$4,$5,$6,'Pending',0,now())
                """, ct, Guid.NewGuid(), tenant, recipientEmployeeId, code, subject, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Uygulama içi bildirim yazılamadı ({Code})", code);
        }
    }

    /// <summary>
    /// Alıcının kayıtlı diline (Profil › TR/EN; notification_preferences) göre bildirim. "Email" kanalında
    /// adres çalışan kaydından alınır, notification-service gönderir. Bağlantı mutlak olmalı.
    /// </summary>
    public async Task<bool> LocalizedAsync(string tenant, Guid recipientEmployeeId, string subjectTr, string subjectEn, string bodyTr, string bodyEn,
        string code, CancellationToken ct, string channel = "InApp", string? actionUrl = null, string? actionLabelTr = null, string? actionLabelEn = null)
    {
        try
        {
            bool en;
            try
            {
                en = await _sql.ScalarAsync("SELECT \"Language\" FROM notification_preferences WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2",
                    ct, tenant, recipientEmployeeId) as string == "en";
            }
            catch (PostgresException) { en = false; }
            string? email = null;
            if (channel == "Email")
            {
                email = await _sql.ScalarAsync("SELECT \"Email\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, tenant, recipientEmployeeId) as string;
                if (string.IsNullOrWhiteSpace(email)) return false;
            }
            await _sql.ExecuteAsync(
                """
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","Language","ActionUrl","ActionLabel")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'Pending',0,now(),$9,$10,$11)
                """, ct, Guid.NewGuid(), tenant, recipientEmployeeId, email, channel, code, en ? subjectEn : subjectTr, en ? bodyEn : bodyTr,
                en ? "en" : "tr", actionUrl, actionUrl is null ? null : en ? actionLabelEn : actionLabelTr);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Bildirim yazılamadı ({Code})", code);
            return false;
        }
    }
}

/// <summary>Tüm denetleyiciler için ortak yardımcılar.</summary>
[ApiController]
public abstract class AppController : ControllerBase
{
    protected UserInfo Me => User.Info();

    protected string Tenant =>
        HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantSlug
        ?? throw new TenantMissingException();

    protected PeopleDirectory People => HttpContext.RequestServices.GetRequiredService<PeopleDirectory>();
    protected Sql Db => HttpContext.RequestServices.GetRequiredService<Sql>();

    /// <summary>Oturumdaki kullanıcının çalışan kaydı (yoksa null — ör. platform yöneticisi).</summary>
    protected async Task<Person?> MyPersonAsync(CancellationToken ct)
        => await People.FindByUserAsync(Tenant, Me.UserId, ct);
}

public sealed class TenantMissingException : Exception
{
    public TenantMissingException() : base("Kiracı bilgisi jetonda yok") { }
}

/// <summary>TenantMissingException → 400; diğer hatalar olduğu gibi.</summary>
public sealed class TenantMissingFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is TenantMissingException)
        {
            context.Result = new BadRequestObjectResult(new
            {
                message = "Oturumunuzda şirket bilgisi yok. Bir şirket seçip yeniden giriş yapın.",
                code = "tenant_missing",
            });
            context.ExceptionHandled = true;
        }
    }
}
