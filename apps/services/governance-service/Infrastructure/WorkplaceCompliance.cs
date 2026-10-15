using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GovernanceService.Infrastructure;

/* ======================================================================
 * İşyeri uyumu (Dalga 5c) — saf yardımcılar: iş günü takvimi, hedef kitle
 * kuralları, etik hattı takip kodu, disiplin savunma metni, İSG kuralları.
 * Denetleyiciler (Controllers/WorkplaceCompliance*.cs) bunları kullanır;
 * birim testleri doğrudan çağırır.
 * ==================================================================== */

/// <summary>Hafta sonu ve kiracının resmî tatilleri (leave_public_holidays) hariç iş günü hesabı.</summary>
public static class BusinessCalendar
{
    public static bool IsBusinessDay(DateOnly d, IReadOnlySet<DateOnly>? holidays) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (holidays is null || !holidays.Contains(d));

    /// <summary>start'tan SONRAKİ n'inci iş günü (start'ın kendisi sayılmaz).</summary>
    public static DateOnly AddBusinessDays(DateOnly start, int days, IReadOnlySet<DateOnly>? holidays = null)
    {
        var d = start;
        while (days > 0)
        {
            d = d.AddDays(1);
            if (IsBusinessDay(d, holidays)) days--;
        }
        return d;
    }

    public static async Task<HashSet<DateOnly>> HolidaysAsync(Sql sql, string tenant, DateOnly from, CancellationToken ct)
    {
        try
        {
            return (await sql.QueryAsync(
                "SELECT \"Date\" FROM leave_public_holidays WHERE \"TenantSlug\" = $1 AND \"Date\" BETWEEN $2 AND $3",
                r => r.GetFieldValue<DateOnly>(0), ct, tenant, from, from.AddDays(60))).ToHashSet();
        }
        catch (Npgsql.PostgresException)
        {
            return new HashSet<DateOnly>(); // tatil tablosu yoksa yalnızca hafta sonu
        }
    }

    public static DateOnly TodayTr => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
}

/// <summary>Duyuru ve kütüphane belgesi hedef kitlesi.</summary>
public static class Audience
{
    public const string All = "All", Managers = "Managers", Hr = "Hr", Departments = "Departments";
    public static readonly string[] DocumentAudiences = { All, Managers, Hr, Departments };
    public static readonly string[] AnnouncementAudiences = { All, Departments };

    /// <summary>
    /// Kullanıcı bu kitleye dâhil mi? İK her şeyi görür (yönetir); "Managers" yönetici rolü,
    /// "Hr" yalnızca İK, "Departments" kişinin güncel departmanı listedeyse.
    /// </summary>
    public static bool CanSee(string audience, IReadOnlyCollection<Guid> departmentIds, bool isHr, bool isManager, Guid? myDepartment) =>
        isHr || audience switch
        {
            All => true,
            Managers => isManager,
            Hr => false,
            Departments => myDepartment is { } d && departmentIds.Contains(d),
            _ => false,
        };

    /// <summary>
    /// İstatistik için hedef kitledeki çalışanlar. Rol bilgisi veritabanında olmadığından
    /// "Managers" = departman başları + ekip liderleri; "Hr" hesaplanamaz (null).
    /// </summary>
    public static List<Person>? Members(string audience, IReadOnlyCollection<Guid> departmentIds, IReadOnlyList<Person> people, IReadOnlySet<Guid> leaders) =>
        audience switch
        {
            All => people.ToList(),
            Departments => people.Where(p => p.DepartmentId is { } d && departmentIds.Contains(d)).ToList(),
            Managers => people.Where(p => leaders.Contains(p.Id)).ToList(),
            _ => null,
        };

    public static async Task<HashSet<Guid>> LeadersAsync(Sql sql, string tenant, CancellationToken ct) =>
        (await sql.QueryAsync("""
            SELECT "HeadEmployeeId" FROM organization_departments WHERE "TenantSlug" = $1 AND "HeadEmployeeId" IS NOT NULL
            UNION SELECT "LeadEmployeeId" FROM organization_teams WHERE "TenantSlug" = $1 AND "LeadEmployeeId" IS NOT NULL
            """, r => r.GetGuid(0), ct, tenant)).ToHashSet();
}

/// <summary>Toplu uygulama içi bildirim (yalnızca başlık; kişisel veri yok).</summary>
public static class BulkNotify
{
    public static async Task<int> InAppAsync(Sql sql, string tenant, IEnumerable<Guid> employeeIds, string subject, string body, string code, CancellationToken ct)
    {
        var ids = employeeIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;
        return await sql.ExecuteAsync("""
            INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
            SELECT gen_random_uuid(), $1, x, NULL, 'InApp', $2, $3, $4, 'Pending', 0, now() FROM unnest($5::uuid[]) AS x
            """, ct, tenant, code, subject, body, ids);
    }
}

public static class BulkNotifyLocalized
{
    /// <summary>G2: toplu bildirim, her alıcının kayıtlı diline göre (tr varsayılan).</summary>
    public static async Task<int> InAppAsync(Sql sql, string tenant, IEnumerable<Guid> employeeIds, string subjectTr, string subjectEn, string bodyTr, string bodyEn, string code, CancellationToken ct)
    {
        var ids = employeeIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;
        return await sql.ExecuteAsync("""
            INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","Language")
            SELECT gen_random_uuid(), $1, x, NULL, 'InApp', $2,
                   CASE WHEN p."Language" = 'en' THEN $4 ELSE $3 END, CASE WHEN p."Language" = 'en' THEN $6 ELSE $5 END,
                   'Pending', 0, now(), CASE WHEN p."Language" = 'en' THEN 'en' ELSE 'tr' END
            FROM unnest($7::uuid[]) AS x LEFT JOIN notification_preferences p ON p."TenantSlug" = $1 AND p."EmployeeId" = x
            """, ct, tenant, code, subjectTr, subjectEn, bodyTr, bodyEn, ids);
    }
}

/// <summary>Hassas görüntüleme/işlem denetim satırı (audit_log değiştirilemez).</summary>
public static class ComplianceAudit
{
    public static Task WriteAsync(Sql sql, string tenant, string entity, string id, string action, object changes, string userId, string userName, CancellationToken ct) =>
        sql.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
            VALUES ($1,'governance-service',$2,$3,$4,$5::jsonb,$6,$7,now())
            """, ct, tenant, entity, id, action, JsonSerializer.Serialize(changes), userId, userName);
}

/// <summary>Bellek içi kayan pencere sayacı (anahtar başına). Tek örnekli servis için yeterli.</summary>
public sealed class WindowLimiter
{
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> _hits = new();
    private readonly int _max;
    private readonly TimeSpan _window;
    public WindowLimiter(int max, TimeSpan window) { _max = max; _window = window; }

    /// <summary>İzin verilirse true; sayaç artar.</summary>
    public bool Allow(string key, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        if (_hits.Count > 50_000) _hits.Clear();
        var h = _hits.AddOrUpdate(key, _ => (t, 1), (_, v) => t - v.Window > _window ? (t, 1) : (v.Window, v.Count + 1));
        return h.Count <= _max;
    }
}

/// <summary>Etik hattı takip kodu: 80 bit rastgele, yalnızca SHA-256 özeti saklanır.</summary>
public static class EthicsCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 karakter, karışmayanlar

    public static string New()
    {
        var b = RandomNumberGenerator.GetBytes(16);
        var chars = b.Select(x => Alphabet[x & 31]).ToArray(); // 32'ye tam bölünür: sapma yok
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..16]}";
    }

    /// <summary>Boşluk/tire temizlenir, büyük harfe çevrilir.</summary>
    public static string Normalize(string? code) =>
        new string((code ?? "").Where(char.IsLetterOrDigit).Select(c => char.ToUpperInvariant(c)).ToArray());

    /// <summary>SHA-256 (hex, küçük harf) — normalleştirilmiş kod üzerinden.</summary>
    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))).ToLowerInvariant();

    public static bool LooksValid(string? code) => Normalize(code) is { Length: 16 } n && n.All(c => Alphabet.Contains(c));

    public static readonly Dictionary<string, string> Categories = new()
    {
        ["Bribery"] = "Rüşvet ve yolsuzluk",
        ["Fraud"] = "Dolandırıcılık / usulsüzlük",
        ["Harassment"] = "Taciz ve mobbing",
        ["Discrimination"] = "Ayrımcılık",
        ["ConflictOfInterest"] = "Çıkar çatışması",
        ["Safety"] = "İş sağlığı ve güvenliği ihlali",
        ["DataPrivacy"] = "Kişisel veri / gizlilik ihlali",
        ["Other"] = "Diğer",
    };

    public static readonly string[] Statuses = { "Received", "InReview", "Closed" };
}

/// <summary>İSG kuralları ve yetkileri.</summary>
public static class Osh
{
    /// <summary>İşyeri hekimi: sağlık notlarını yalnızca bu rol okur/yazar.</summary>
    public static bool IsPhysician(UserInfo me) => me.Roles.Contains("osh-physician") || me.Roles.Contains("ext-osh-physician");
    /// <summary>İş güvenliği uzmanı: olay ve eğitimleri yönetir; sağlık notlarını göremez.</summary>
    public static bool IsSpecialist(UserInfo me) => me.Roles.Contains("osh-specialist") || me.Roles.Contains("ext-osh-specialist");
    /// <summary>Olay/eğitim/muayene sonucu yönetimi: İK yöneticisi (hr-admin, tenant-admin), İSG uzmanı veya hekim.</summary>
    public static bool CanManage(UserInfo me) => me.IsHr || IsSpecialist(me) || IsPhysician(me);

    public static readonly string[] Kinds = { "Accident", "NearMiss" };
    public static readonly string[] Results = { "Fit", "Unfit", "Conditional" };
    public static readonly string[] ExamTypes = { "PreEmployment", "Periodic", "ReturnToWork", "JobChange" };

    /// <summary>
    /// SGK iş kazası bildirimi (5510 s. K. m.13): kazadan sonraki 3 iş günü içinde.
    /// </summary>
    public static DateOnly SgkDeadline(DateOnly occurredOn, IReadOnlySet<DateOnly>? holidays = null) =>
        BusinessCalendar.AddBusinessDays(occurredOn, 3, holidays);

    /// <summary>
    /// Periyodik muayene aralığı (İşyeri Sağlık ve Güvenlik Birimleri Yönetmeliği):
    /// çok tehlikeli 1 yıl, tehlikeli 3 yıl, az tehlikeli 5 yıl.
    /// </summary>
    public static DateOnly NextExam(DateOnly examDate, string? hazardClass) => hazardClass switch
    {
        "VeryHazardous" => examDate.AddYears(1),
        "LessHazardous" => examDate.AddYears(5),
        _ => examDate.AddYears(3),
    };

    /// <summary>Overdue | DueSoon (30 gün) | Ok | None</summary>
    public static string DueState(DateOnly? due, DateOnly today, int soonDays = 30) =>
        due is null ? "None" : due < today ? "Overdue" : due <= today.AddDays(soonDays) ? "DueSoon" : "Ok";
}

/// <summary>Disiplin süreci: savunma istem yazısı, asgari süre, adli sicil uyarısı.</summary>
public static class Disciplinary
{
    public const int MinDefenceBusinessDays = 2;

    public static readonly Dictionary<string, string> Categories = new()
    {
        ["Attendance"] = "Devamsızlık / geç gelme",
        ["Conduct"] = "Davranış ve iş disiplini",
        ["Performance"] = "Performans yetersizliği",
        ["PolicyViolation"] = "Şirket politikası ihlali",
        ["Safety"] = "İSG kurallarına aykırılık",
        ["Other"] = "Diğer",
    };

    public static readonly Dictionary<string, string> Decisions = new()
    {
        ["NoAction"] = "İşlem yapılmasına gerek yok",
        ["Warning"] = "Sözlü uyarı",
        ["WrittenWarning"] = "Yazılı ihtar",
        ["TerminationRecommendation"] = "Fesih önerisi",
    };

    public static DateOnly MinDeadline(DateOnly today, IReadOnlySet<DateOnly>? holidays = null) =>
        BusinessCalendar.AddBusinessDays(today, MinDefenceBusinessDays, holidays);

    private static readonly CultureInfo Tr = new("tr-TR");

    /// <summary>Savunma istem yazısı (İş Kanunu m.19 uyarınca yazılı savunma).</summary>
    public static string DefenceNotice(string employeeName, string? company, DateOnly incidentDate, string categoryLabel, string description, DateOnly deadline) =>
        $"""
        Sayın {employeeName},

        {incidentDate.ToString("dd.MM.yyyy", Tr)} tarihinde meydana geldiği bildirilen ve "{categoryLabel}" kapsamında değerlendirilen aşağıdaki olayla ilgili olarak yazılı savunmanızın alınması gerekmektedir:

        {description.Trim()}

        Yazılı savunmanızı en geç {deadline.ToString("dd.MM.yyyy", Tr)} ({deadline.ToString("dddd", Tr)}) günü mesai bitimine kadar HR360 üzerinden (Günlük iş › Savunmalarım) iletmenizi rica ederiz. Bu süre içinde savunma vermemeniz hâlinde savunma hakkınızı kullanmaktan vazgeçmiş sayılacağınızı ve değerlendirmenin mevcut bilgiler üzerinden yapılacağını bildiririz.

        Savunmanızda yalnızca olayla ilgili açıklamalarınıza yer vermeniz yeterlidir; sağlık bilgisi, adli sicil kaydı gibi özel nitelikli kişisel verilerinizi paylaşmanız gerekmez.

        {(string.IsNullOrWhiteSpace(company) ? "İnsan Kaynakları" : company + " — İnsan Kaynakları")}
        """;

    /// <summary>
    /// Adli sicil / mahkûmiyet bilgisi disiplin dosyasında tutulmamalıdır (KVKK m.6 özel nitelikli veri).
    /// Engellemez; kullanıcıya uyarı döndürür.
    /// </summary>
    public static string[] CriminalRecordWarnings(params string?[] texts)
    {
        var joined = string.Join(" ", texts.Where(t => !string.IsNullOrWhiteSpace(t))).ToLower(Tr);
        if (joined.Length == 0) return Array.Empty<string>();
        string[] keywords = { "sabıka", "adli sicil", "mahkûm", "mahkum", "hapis cezası", "ceza mahkemesi", "hükümlü", "cezaevi", "savcılık soruşturması", "iddianame" };
        return keywords.Any(joined.Contains)
            ? new[] { "Metinde adli sicil / ceza mahkûmiyeti ile ilgili ifade var. Bu bilgiler özel nitelikli kişisel veridir (KVKK m.6) ve disiplin dosyasında tutulmamalıdır; lütfen metni düzeltin." }
            : Array.Empty<string>();
    }
}
