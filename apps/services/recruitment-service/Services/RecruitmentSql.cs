using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecruitmentService.Data;

namespace RecruitmentService.Services;

/// <summary>
/// Paylaşılan veritabanındaki diğer servis tablolarına küçük, salt-okunur sorgular
/// (çalışan, departman başı, kiracı adı) ve bildirim/denetim/imha tutanağı satırları.
/// Desen: compensation-service PayrollEcosystemController (Notify/AuditAsync).
/// </summary>
public static class RecruitmentSql
{
    public sealed class TenantRow
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }

    public sealed class NameRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public static async Task<Dictionary<Guid, string>> DepartmentNamesAsync(this RecruitmentDbContext db, string tenant, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<NameRow>(
            "SELECT \"Id\", \"Name\" FROM organization_departments WHERE \"TenantSlug\" = {0}", tenant).ToListAsync(ct))
        .ToDictionary(r => r.Id, r => r.Name);

    public sealed class PersonRow
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
    }

    public static string? UserId(ClaimsPrincipal u) =>
        u.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? u.FindFirst("sub")?.Value;

    public static bool IsHr(ClaimsPrincipal u) =>
        u.IsInRole("hr-admin") || u.IsInRole("tenant-admin") || u.IsInRole("platform-admin") || u.IsInRole("ext-recruitment-publish");

    public static async Task<TenantRow?> TenantAsync(this RecruitmentDbContext db, string slug, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<TenantRow>(
            "SELECT \"Name\", \"Status\" FROM platform_tenants WHERE \"Slug\" = {0} LIMIT 1", slug).FirstOrDefaultAsync(ct);

    public static async Task<Guid?> MyEmployeeIdAsync(this RecruitmentDbContext db, string? tenant, string? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(userId)) return null;
        return await db.Database.SqlQueryRaw<Guid?>(
            "SELECT \"Id\" AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"KeycloakUserId\" = {1} LIMIT 1",
            tenant, userId).FirstOrDefaultAsync(ct);
    }

    public static async Task<Guid?> DepartmentHeadAsync(this RecruitmentDbContext db, string tenant, Guid departmentId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<Guid?>(
            "SELECT \"HeadEmployeeId\" AS \"Value\" FROM organization_departments WHERE \"TenantSlug\" = {0} AND \"Id\" = {1} LIMIT 1",
            tenant, departmentId).FirstOrDefaultAsync(ct);

    public static async Task<string?> DepartmentNameAsync(this RecruitmentDbContext db, string tenant, Guid departmentId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<string>(
            "SELECT \"Name\" AS \"Value\" FROM organization_departments WHERE \"TenantSlug\" = {0} AND \"Id\" = {1} LIMIT 1",
            tenant, departmentId).FirstOrDefaultAsync(ct);

    /// <summary>Kiracının etkin (ayrılmamış) çalışanlarından verilenleri döner.</summary>
    public static async Task<List<PersonRow>> PeopleAsync(this RecruitmentDbContext db, string tenant, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        return await db.Database.SqlQueryRaw<PersonRow>(
            "SELECT \"Id\", \"FirstName\", \"LastName\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = ANY({1}) AND \"Status\" <> 'Terminated'",
            tenant, ids.ToArray()).ToListAsync(ct);
    }

    /// <summary>Uygulama içi bildirim (çalışana). Kişisel veri en aza indirilir; ücret asla yazılmaz.</summary>
    public static async Task NotifyAsync(this RecruitmentDbContext db, string tenant, Guid employeeId, string subject, string body, string code, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ({0},{1},{2},NULL,'InApp',{3},{4},{5},'Pending',0,now())
                """, new object[] { Guid.NewGuid(), tenant, employeeId, code, subject, body }, ct);
        }
        catch (Exception) { /* bildirim yazılamazsa iş akışı bozulmaz */ }
    }

    /// <summary>
    /// Adaya e-posta: notification-service EmailSenderWorker, Channel='Email' ve RecipientEmail dolu
    /// satırları kiracının SMTP'siyle gönderir. Aday çalışan olmadığı için RecipientEmployeeId boş GUID'dir
    /// (uygulama içi kutuda kimseye görünmez).
    /// </summary>
    public static async Task EmailCandidateAsync(this RecruitmentDbContext db, string tenant, string email, string subject, string body, string code, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ({0},{1},{2},{3},'Email',{4},{5},{6},'Pending',0,now())
                """, new object[] { Guid.NewGuid(), tenant, Guid.Empty, email, code, subject, body }, ct);
        }
        catch (Exception) { }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Hassas görüntüleme/işlem denetim satırı (audit_log değiştirilemez; yalnızca eklenir).</summary>
    public static async Task AuditAsync(this RecruitmentDbContext db, HttpContext? http, string? tenant, string entityType, string entityId,
        string action, object changes, CancellationToken ct)
    {
        try
        {
            var user = http?.User;
            // NOT: EF ham SQL parametresi DBNull kabul etmiyor; IP sütunu bilinçli olarak NULL (herkese açık
            // uçlarda IP adresi saklanmaz), diğer değerler boş olamaz.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'recruitment-service',{1},{2},{3},{4}::jsonb,{5},{6},{7},NULL,now())",
                new object[]
                {
                    tenant ?? "", entityType, entityId, action, JsonSerializer.Serialize(changes, Json),
                    (user is null ? null : UserId(user)) ?? "system",
                    user?.FindFirst("preferred_username")?.Value ?? user?.FindFirst("email")?.Value ?? "system",
                    http?.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? http?.TraceIdentifier ?? "-",
                }, ct);
        }
        catch (Exception ex) { Console.Error.WriteLine($"recruitment-service: denetim kaydı yazılamadı: {ex.Message}"); }
    }

    /// <summary>
    /// Dalga 10 (KVKK imha doğrulaması): silinecek/anonimleşecek aday kaydının özgeçmiş dosyası anahtarını
    /// governance'in silme kuyruğuna yazar; governance bakım turu nesneyi siler ve yokluğunu doğrular.
    /// Anahtar işlendikten sonra kuyrukta yalnızca SHA-256 özeti kalır. Kuyruk tablosu yoksa sessizce geçer.
    /// </summary>
    public static async Task EnqueueResumeDeletionAsync(this RecruitmentDbContext db, string tenant, Guid candidateId, string category, CancellationToken ct)
    {
        try
        {
            var key = await db.Candidates.IgnoreQueryFilters().Where(c => c.Id == candidateId).Select(c => c.ResumeStorageKey).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(key)) return;
            key = key.Trim();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO governance_storage_deletions ("Id","TenantSlug","Category","SourceTable","SourceColumn","StorageKey","KeyHash","Status","CreatedAt")
                VALUES ({0},{1},{2},'recruitment_candidates','ResumeStorageKey',{3},{4},'Pending',now())
                """, new object[] { Guid.NewGuid(), tenant, category, key, hash }, ct);
        }
        catch (Exception ex) { Console.Error.WriteLine($"recruitment-service: dosya silme kuyruğuna yazılamadı: {ex.Message}"); }
    }

    /// <summary>İmha tutanağı (governance /kvkk ekranında görünür).</summary>
    public static async Task DestructionLogAsync(this RecruitmentDbContext db, string tenant, int affected, int retentionMonths, string trigger, string actor, CancellationToken ct,
        string action = "Anonymize")
    {
        if (affected == 0) return;
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO governance_destruction_logs ("Id","TenantSlug","Category","Action","Affected","RetentionMonths","Trigger","Actor","Method","RanAt")
                VALUES ({0},{1},'RecruitmentCandidates',{2},{3},{4},{5},{6},{7},now())
                """, new object[] { Guid.NewGuid(), tenant, action, affected, retentionMonths, trigger, actor,
                    action == "Delete" ? "Veritabanından kalıcı silme (aday başvurusu, mülakat, puan kartı ve teklif kayıtları)"
                        : "Geri döndürülemez anonimleştirme: aday kimlik, iletişim, özgeçmiş ve not alanları silinir; aşama istatistikleri kalır" }, ct);
        }
        catch (Exception) { }
    }
}
