using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace OnboardingService.Services;

/// <summary>
/// Diger modullerin tablolarini (calisan, departman) SALT OKUNUR sorgulamak ve
/// bildirim/denetim satiri yazmak icin ince Npgsql yardimcisi. Tum servisler ayni
/// "hr360_operational" veritabanini paylasir; her sorgu kiraci slug'i ile filtrelenir.
/// </summary>
public sealed class Sql
{
    private readonly NpgsqlDataSource _ds;
    public Sql(NpgsqlDataSource ds) => _ds = ds;
    public NpgsqlDataSource DataSource => _ds;

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
}

public static class ReaderExt
{
    public static string? Str(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    public static Guid? GuidOrNull(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    public static DateOnly? DateOrNull(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
}

/// <summary>Calisan dizini ozeti (ad, e-posta, unvan, departman, departman basi).</summary>
public sealed record PersonInfo(Guid Id, string FirstName, string LastName, string? Email, string? Position,
    Guid? DepartmentId, string? Department, Guid? DepartmentHeadId, string Status)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public static class People
{
    private const string Base = """
        SELECT e."Id", e."FirstName", e."LastName", e."Email", a."PositionTitle", a."DepartmentId", d."Name", d."HeadEmployeeId", e."Status"
        FROM employee_employees e
        LEFT JOIN LATERAL (
            SELECT x."PositionTitle", x."DepartmentId" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id"
            ORDER BY (x."EffectiveTo" IS NULL) DESC, x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        WHERE e."TenantSlug" = $1
        """;

    private static PersonInfo Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Str(3), r.Str(4),
        r.GuidOrNull(5), r.Str(6), r.GuidOrNull(7), r.GetString(8));

    public static async Task<PersonInfo?> FindAsync(Sql sql, string tenant, Guid id, CancellationToken ct) =>
        (await sql.QueryAsync(Base + " AND e.\"Id\" = $2", Map, ct, tenant, id)).FirstOrDefault();

    public static async Task<Dictionary<Guid, PersonInfo>> FindManyAsync(Sql sql, string tenant, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var arr = ids.Distinct().ToArray();
        if (arr.Length == 0) return new();
        return (await sql.QueryAsync(Base + " AND e.\"Id\" = ANY($2)", Map, ct, tenant, arr)).ToDictionary(p => p.Id);
    }
}

/// <summary>Uygulama ici / e-posta bildirimi (notification-service kuyruguna duser).</summary>
public static class Notify
{
    public static async Task InAppAsync(Sql sql, string tenant, Guid recipient, string subject, string body, string code, CancellationToken ct)
    {
        try
        {
            await sql.ExecuteAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ($1,$2,$3,NULL,'InApp',$4,$5,$6,'Pending',0,now())
                """, ct, Guid.NewGuid(), tenant, recipient, code, subject, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* bildirim yazilamazsa is akisi bozulmaz */ }
    }
}

/// <summary>Denetim kaydi (audit_log degistirilemez; yalnizca INSERT).</summary>
public static class Audit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Sql sql, HttpContext http, string? tenant, string entityType, string entityId, string action, object changes)
    {
        var u = http.User;
        var userId = u.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";
        var userName = u.FindFirst("name")?.Value ?? u.FindFirst("preferred_username")?.Value ?? userId;
        try
        {
            await sql.ExecuteAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ($1,'onboarding-service',$2,$3,$4,$5::jsonb,$6,$7,$8,$9,now())", CancellationToken.None,
                tenant, entityType, entityId, action, JsonSerializer.Serialize(changes, Json), userId, userName,
                http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? http.TraceIdentifier,
                http.Request.Headers["X-Real-IP"].FirstOrDefault());
        }
        catch (Exception) { /* denetim yazilamazsa is akisi bozulmaz */ }
    }
}

/// <summary>Zimmet QR kodu: 16 karakter, karisiklik yaratan harfler (0/O, 1/I) yok.</summary>
public static class AssetCodes
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public static string New()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        var c = new char[16];
        for (var i = 0; i < 16; i++) c[i] = Alphabet[b[i] % Alphabet.Length];
        return new string(c);
    }

    public static string Normalize(string? code) =>
        new string((code ?? "").Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>Isletmenin saat dilimine gore "bugun" (HR360_TIMEZONE, varsayilan Europe/Istanbul).</summary>
public static class BusinessClock
{
    private static readonly TimeZoneInfo Zone = Resolve();
    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("HR360_TIMEZONE") ?? "Europe/Istanbul"); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);
}

/// <summary>
/// Ilk gun karsilama sablonu. Yer tutucular: {ad}, {baslangic}, {yonetici}, {buddy}, {konum}
/// (Ingilizce esdegerleri {name}, {startDate}, {manager}, {buddy}, {location}). Hassas veri
/// (ucret, TCKN, adres) yer tutucusu bilerek YOKTUR.
/// </summary>
public static class WelcomeTemplate
{
    public const string DefaultSubject = "Aramıza hoş geldin, {ad}!";
    public const string DefaultBody =
        "Merhaba {ad},\n\nBugün ({baslangic}) ilk iş günün. Yöneticin: {yonetici}. " +
        "Uyum sürecinde yol arkadaşın (buddy) {buddy} sana eşlik edecek.\n" +
        "Buluşma yeri: {konum}.\n\nİlk gün görevlerini HR360'ta İşe alışma ekranında bulabilirsin. İyi bir başlangıç dileriz!";

    public static string Render(string template, string firstName, DateOnly start, string? manager, string? buddy, string? location)
    {
        var map = new Dictionary<string, string>
        {
            ["{ad}"] = firstName, ["{name}"] = firstName,
            ["{baslangic}"] = start.ToString("dd.MM.yyyy"), ["{startDate}"] = start.ToString("dd.MM.yyyy"),
            ["{yonetici}"] = string.IsNullOrWhiteSpace(manager) ? "İK ekibi" : manager, ["{manager}"] = string.IsNullOrWhiteSpace(manager) ? "İK ekibi" : manager,
            ["{buddy}"] = string.IsNullOrWhiteSpace(buddy) ? "İK ekibi" : buddy,
            ["{konum}"] = string.IsNullOrWhiteSpace(location) ? "İK ekibi bilgilendirecek" : location,
            ["{location}"] = string.IsNullOrWhiteSpace(location) ? "İK ekibi bilgilendirecek" : location,
        };
        foreach (var (k, v) in map) template = template.Replace(k, v, StringComparison.OrdinalIgnoreCase);
        return template;
    }
}
