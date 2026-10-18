using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prometheus;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Tenancy;

namespace GovernanceService.Infrastructure;

/* ======================================================================
 * Güvenlik dalgası 2B — veri koruma.
 *
 *  - Kiracı güvenlik ayarları (governance_security_settings): bordro görevler
 *    ayrılığı, toplu görüntüleme eşiği/penceresi, isteğe bağlı geçici engel,
 *    uyarı alıcıları.
 *  - Toplu görüntüleme (sızdırma) dedektörü: denetim kaydındaki erişim satırlarından
 *    (Revealed, SensitiveViewed, Exported, Viewed) bir kullanıcının kısa sürede çok
 *    sayıda farklı kaydı açtığını yakalar; denetim kaydı + yönetici bildirimi +
 *    Prometheus sayacı. Varsayılan olarak engellemez.
 *  - Filigran ve iz kodu: indirilen/yazdırılan kişisel veri içeren çıktılara
 *    "<ad soyad> · <tarih saat> · <iz kodu>" yazılır; kod denetim kaydına bağlıdır.
 *  - Herkese açık formlar için bot koruması (FormGuard): HMAC imzalı zaman jetonu +
 *    görünmez alan; üçüncü taraf CAPTCHA yok.
 * ==================================================================== */

/// <summary>Kiracının veri koruma ayarları. Satır yoksa varsayılanlar geçerlidir.</summary>
public sealed record SecuritySettings(
    bool PayrollSod, string? PayrollSodReason,
    int MassViewThreshold, int MassViewWindowMinutes, bool MassViewBlock,
    Guid[] AlertEmployeeIds, DateTime? UpdatedAt, string? UpdatedBy)
{
    public static SecuritySettings Default => new(true, null, 50, 10, false, Array.Empty<Guid>(), null, null);

    /// <summary>"Geçici engel" açıksa uyarıdan sonra kullanıcının hassas alan açması bu süre engellenir.</summary>
    public const int BlockMinutes = 15;

    /// <summary>Girdi sınırları: eşik 10-1000 farklı kayıt, pencere 1-60 dakika.</summary>
    public static string? Validate(int threshold, int windowMinutes) =>
        threshold is < 10 or > 1000 ? "Eşik 10 ile 1000 arasında olmalı."
        : windowMinutes is < 1 or > 60 ? "Zaman penceresi 1 ile 60 dakika arasında olmalı."
        : null;
}

public static class SecuritySettingsStore
{
    public static async Task<SecuritySettings> LoadAsync(Sql db, string tenant, CancellationToken ct)
    {
        try
        {
            var row = await db.QueryAsync("""
                SELECT "PayrollSod","PayrollSodReason","MassViewThreshold","MassViewWindowMinutes","MassViewBlock","AlertEmployeeIds","UpdatedAt","UpdatedBy"
                FROM governance_security_settings WHERE "TenantSlug" = $1
                """, r => new SecuritySettings(r.GetBoolean(0), r.Str(1), r.GetInt32(2), r.GetInt32(3), r.GetBoolean(4),
                    r.IsDBNull(5) ? Array.Empty<Guid>() : r.GetFieldValue<Guid[]>(5), r.Ts(6), r.Str(7)), ct, tenant);
            return row.FirstOrDefault() ?? SecuritySettings.Default;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            return SecuritySettings.Default; // tablo henüz yok (göç uygulanmadı)
        }
    }

    public static Task SaveAsync(Sql db, string tenant, SecuritySettings s, string userName, CancellationToken ct) =>
        db.ExecuteAsync("""
            INSERT INTO governance_security_settings ("Id","TenantSlug","PayrollSod","PayrollSodReason","MassViewThreshold","MassViewWindowMinutes","MassViewBlock","AlertEmployeeIds","UpdatedAt","UpdatedBy")
            VALUES (gen_random_uuid(), $1, $2, $3, $4, $5, $6, $7, now(), $8)
            ON CONFLICT ("TenantSlug") DO UPDATE SET "PayrollSod" = $2, "PayrollSodReason" = $3, "MassViewThreshold" = $4,
                "MassViewWindowMinutes" = $5, "MassViewBlock" = $6, "AlertEmployeeIds" = $7, "UpdatedAt" = now(), "UpdatedBy" = $8
            """, ct, tenant, s.PayrollSod, s.PayrollSodReason, s.MassViewThreshold, s.MassViewWindowMinutes, s.MassViewBlock, s.AlertEmployeeIds, userName);

    /// <summary>
    /// Güvenlik uyarısı alıcıları: ayarlardaki kişiler + onay ayarlarındaki İK onaycısı (rol bilgisi
    /// Keycloak'ta olduğundan "tüm şirket yöneticileri" veritabanından okunamaz). Yalnızca etkin çalışanlar.
    /// </summary>
    public static async Task<List<Guid>> RecipientsAsync(Sql db, string tenant, SecuritySettings s, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(s.AlertEmployeeIds);
        try
        {
            foreach (var g in await db.QueryAsync("""SELECT "HrApproverEmployeeId" FROM workflow_settings WHERE "TenantSlug" = $1 AND "HrApproverEmployeeId" IS NOT NULL""",
                         r => r.GetGuid(0), ct, tenant))
                ids.Add(g);
        }
        catch (Npgsql.PostgresException) { /* workflow_settings yoksa yalnızca ayardaki alıcılar */ }
        if (ids.Count == 0) return new();
        return await db.QueryAsync("""
            SELECT "Id" FROM employee_employees WHERE "TenantSlug" = $1 AND "Id" = ANY($2) AND "Status" <> 'Terminated'
            """, r => r.GetGuid(0), ct, tenant, ids.ToArray());
    }

    /// <summary>Kullanıcı toplu görüntüleme uyarısı nedeniyle geçici olarak engelli mi (yalnızca ayar açıksa yazılır).</summary>
    public static async Task<bool> IsBlockedAsync(Sql db, string tenant, string userId, CancellationToken ct)
    {
        try
        {
            return await db.ScalarAsync("""
                SELECT 1 FROM governance_security_alerts WHERE "TenantSlug" = $1 AND "UserId" = $2 AND "BlockedUntil" > now() LIMIT 1
                """, ct, tenant, userId) is not null;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            return false;
        }
    }
}

/// <summary>Filigran metni ve iz kodu (indiren kişi · Türkiye saatiyle zaman · kod).</summary>
public static class Watermark
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeZoneInfo Istanbul = FindIstanbul();

    private static TimeZoneInfo FindIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT"); }
    }

    /// <summary>"K7P2-MX9Q" biçiminde 40 bitlik rastgele kod (compensation-service TraceCode ile aynı biçim).</summary>
    public static string NewCode()
    {
        var b = RandomNumberGenerator.GetBytes(8);
        var c = b.Select(x => Alphabet[x & 31]).ToArray();
        return $"{new string(c, 0, 4)}-{new string(c, 4, 4)}";
    }

    /// <summary>Kullanıcının yazdığı kodu biçime getirir ("k7p2mx9q" → "K7P2-MX9Q"); geçersizse null.</summary>
    public static string? NormalizeCode(string? code)
    {
        var s = new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (s.Length != 8 || s.Any(ch => !Alphabet.Contains(ch))) return null;
        return $"{s[..4]}-{s[4..]}";
    }

    /// <summary>Filigran: "Ayşe Yılmaz · 06.10.2026 14:32 · K7P2-MX9Q" (zaman Türkiye saatiyle).</summary>
    public static string Text(string name, DateTimeOffset at, string code)
    {
        var local = TimeZoneInfo.ConvertTime(at, Istanbul);
        var who = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();
        if (who.Length > 80) who = who[..80];
        return $"{who} · {local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} · {code}";
    }

    /// <summary>
    /// İz kodunu taşıyan denetim satırı (audit_log değiştirilemez; kod Changes->>'traceCode'). EntityId, varsa
    /// çıktının konusu (ör. pusula kimliği) olur: toplu görüntüleme dedektörü farklı kişilerin çıktılarını ayrı sayar.
    /// </summary>
    public static Task AuditAsync(Sql db, string tenant, string kind, string? subject, string? format, int? rows, string code, string userId, string userName, CancellationToken ct) =>
        ComplianceAudit.WriteAsync(db, tenant, "Download", subject ?? kind, "Exported",
            new { traceCode = code, kind, subject, format, rows }, userId, userName, ct);
}

/// <summary>
/// İz kodu isteğinin doğrulaması: yalnızca istemcinin gerçekten ürettiği çıktı türleri (tablo CSV'si, içe/dışa
/// aktarım XLSX'i, belge, bordro pusulası, sertifika) kabul edilir; her tür kendi görüntüleme izniyle korunur.
/// Böylece herhangi bir kullanıcı denetim kaydına keyfi "Exported" satırı yazamaz.
/// </summary>
public static class TracePolicy
{
    public enum Kind { Table, Xlsx, Document, Payslip, Certificate }

    public static readonly string[] Formats = { "csv", "xlsx", "print", "pdf" };

    private static readonly string[] Hr = { "hr-admin", "tenant-admin", "platform-admin" };
    /// <summary>compensation-service IsPayrollViewer ile aynı.</summary>
    public static readonly string[] CompensationView = { "hr-admin", "tenant-admin", "platform-admin", "ext-compensation-view" };
    public static readonly string[] RecruitmentView = { "manager", "hr-admin", "tenant-admin", "platform-admin", "ext-recruitment-view", "ext-recruitment-candidates", "ext-recruitment-publish" };
    public static readonly string[] EmployeeListView = { "manager", "accounting", "hr-admin", "tenant-admin", "platform-admin", "ext-employee-viewAll", "ext-employee-create", "ext-employee-manage", "ext-compensation-view", "ext-expense-markPaid" };
    public static readonly string[] ImportExport = { "hr-admin", "tenant-admin", "platform-admin", "ext-employee-manage" };
    public static readonly string[] DocumentPrint = { "hr-admin", "tenant-admin", "platform-admin", "ext-document-manage", "manager", "ext-recruitment-candidates", "ext-recruitment-publish" };
    public static readonly string[] LearningOthers = { "manager", "hr-admin", "tenant-admin", "platform-admin", "ext-learning-manage" };

    /// <summary>Arayüzdeki tablo dışa aktarımları (DataTable exportFileName) → gereken roller (null = oturum açmış herkes; liste zaten kişiye göre süzülür).</summary>
    private static readonly Dictionary<string, string[]?> Tables = new(StringComparer.Ordinal)
    {
        ["adaylar"] = RecruitmentView, ["ilanlar"] = RecruitmentView, ["calisanlar"] = EmployeeListView, ["kiracilar"] = new[] { "platform-admin" },
        ["sirketler"] = null, ["onay-talepleri"] = null, ["izin-talepleri"] = null, ["masraf-talepleri"] = null, ["ik-vakalari"] = null,
        ["dokuman-kayitlari"] = null, ["egitim-katalogu"] = null, ["sertifikalar"] = null, ["onboarding-planlari"] = null,
        ["zimmet-envanteri"] = null, ["ic-basvurular"] = null,
    };

    private static readonly (System.Text.RegularExpressions.Regex Rx, string[]? Roles)[] TablePatterns =
    {
        (new(@"^bordro-\d{4}-\d{1,2}$"), CompensationView),
        (new(@"^ucret-bantlari-\d{4}$"), CompensationView),
        (new(@"^zam-simulasyonu-\d{4}$"), CompensationView),
        (new(@"^puantaj-\d{4}-\d{2}$"), null),
    };

    private static readonly System.Text.RegularExpressions.Regex XlsxId = new("^[a-z0-9_-]{1,40}$");
    // Doğrulama kodu ya da dış kurumun sertifika numarası (ör. "AWS-123/45 B"): harf, rakam ve . _ / : # - boşluk.
    private static readonly System.Text.RegularExpressions.Regex CertCode = new(@"^[\p{L}\p{N}._/:# -]{1,100}$");

    public sealed record Decision(Kind? Kind, string? Error, string[]? RequiredRoles, string? Subject, string? Format);

    private static bool Any(IReadOnlySet<string> roles, string[]? need) => need is null || need.Any(roles.Contains);

    /// <summary>
    /// Türü, konuyu ve biçimi doğrular; rol gereğini döner. Sahiplik (kendi pusulası/sertifikası) veritabanında ayrıca
    /// denetlenir: Payslip ve Certificate için RequiredRoles, kişi çıktının sahibi DEĞİLSE gereken rollerdir.
    /// </summary>
    public static Decision Evaluate(string? kindRaw, string? subjectRaw, string? formatRaw)
    {
        var kind = (kindRaw ?? "").Trim();
        var subject = string.IsNullOrWhiteSpace(subjectRaw) ? null : subjectRaw.Trim();
        var format = string.IsNullOrWhiteSpace(formatRaw) ? null : formatRaw.Trim().ToLowerInvariant();
        if (format is not null && !Formats.Contains(format)) return new(null, "format", null, null, null);
        if (subject is not null && (subject.Length > 100 || subject.Any(char.IsControl))) return new(null, "subject", null, null, null);

        if (kind.StartsWith("table:", StringComparison.Ordinal))
        {
            var name = kind["table:".Length..];
            if (subject is not null) return new(null, "subject", null, null, null);
            if (Tables.TryGetValue(name, out var roles)) return new(Kind.Table, null, roles, null, format);
            foreach (var (rx, r) in TablePatterns)
                if (rx.IsMatch(name)) return new(Kind.Table, null, r, null, format);
            return new(null, "kind", null, null, null);
        }
        if (kind.StartsWith("xlsx:", StringComparison.Ordinal))
            return XlsxId.IsMatch(kind["xlsx:".Length..]) && subject is null
                ? new(Kind.Xlsx, null, ImportExport, null, format) : new(null, "kind", null, null, null);
        switch (kind)
        {
            case "document":
                return new(Kind.Document, null, DocumentPrint, subject, format);
            case "payslip":
                return Guid.TryParse(subject, out var g)
                    ? new(Kind.Payslip, null, CompensationView, g.ToString(), format) : new(null, "subject", null, null, null);
            case "certificate":
                if (subject is not null && !CertCode.IsMatch(subject)) return new(null, "subject", null, null, null);
                return new(Kind.Certificate, null, LearningOthers, subject, format);
            default:
                return new(null, "kind", null, null, null);
        }
    }

    /// <summary>Rol denetimi (sahiplik dışındaki durum).</summary>
    public static bool RolesAllow(Decision d, IReadOnlySet<string> roles) => Any(roles, d.RequiredRoles);
}

/* ---------------------------------------------------------------- toplu görüntüleme */

/// <summary>Denetim kaydından okunan bir erişim: kim, hangi kayıt, en son ne zaman.</summary>
public sealed record AccessEvent(string UserId, string? UserName, string EntityKey, DateTime At);

/// <summary>Eşiği aşan kullanıcı.</summary>
public sealed record MassViewHit(string UserId, string? UserName, int Distinct);

public static class MassViewDetector
{
    /// <summary>Sayılan erişim işlemleri (audit_log."Action").</summary>
    public static readonly string[] Actions = { "Revealed", "SensitiveViewed", "Exported", "Viewed" };

    /// <summary>
    /// Kayıt anahtarı: EntityId bir GUID ise (çalışan kimliği çoğunlukla) yalnızca o; böylece aynı çalışanın
    /// farklı ekranlardan açılması tek kayıt sayılır. Değilse "tür:kimlik".
    /// </summary>
    public static string Key(string entityType, string? entityId) =>
        Guid.TryParse(entityId, out var g) ? g.ToString() : $"{entityType}:{entityId}";

    /// <summary>
    /// Pencere içinde <paramref name="threshold"/>'dan FAZLA farklı kayıt açan kullanıcılar. Aynı pencerede zaten
    /// uyarı verilmiş kullanıcı (lastAlert) yeniden bildirilmez. Sistem kullanıcısı sayılmaz.
    /// </summary>
    public static List<MassViewHit> Evaluate(IEnumerable<AccessEvent> events, int threshold, int windowMinutes, DateTime now,
        IReadOnlyDictionary<string, DateTime>? lastAlert = null)
    {
        var from = now.AddMinutes(-windowMinutes);
        return events
            .Where(e => e.At >= from && e.At <= now.AddMinutes(1) && !string.IsNullOrEmpty(e.UserId) && e.UserId != "system")
            .GroupBy(e => e.UserId)
            .Select(g => new MassViewHit(g.Key, g.Select(x => x.UserName).LastOrDefault(n => !string.IsNullOrEmpty(n)),
                g.Select(x => x.EntityKey).Distinct().Count()))
            .Where(h => h.Distinct > threshold)
            .Where(h => lastAlert is null || !lastAlert.TryGetValue(h.UserId, out var t) || t < from)
            .OrderByDescending(h => h.Distinct)
            .ToList();
    }
}

/// <summary>
/// Dakikada bir denetim kaydının son 60 dakikasını tarar; kiracı ayarındaki pencere/eşikle
/// <see cref="MassViewDetector"/> çalıştırır. Uyarı: governance_security_alerts satırı + audit_log
/// ("MassViewDetected") + uyarı alıcılarına uygulama içi bildirim + hr360_mass_view_alerts_total.
/// </summary>
public sealed class MassViewWorker : BackgroundService
{
    public static readonly Counter Alerts = Metrics.CreateCounter("hr360_mass_view_alerts_total",
        "Toplu görüntüleme (olası veri sızdırma) uyarısı sayısı.");
    public static readonly Counter Blocks = Metrics.CreateCounter("hr360_mass_view_blocks_total",
        "Toplu görüntüleme uyarısı sonrası uygulanan geçici engel sayısı.");

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(
        double.TryParse(EnvVar.Or("MASS_VIEW_CHECK_SECONDS", "60"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 5, 3600) : 60);

    private readonly IServiceProvider _sp;
    private readonly ILogger<MassViewWorker> _log;
    public MassViewWorker(IServiceProvider sp, ILogger<MassViewWorker> log) { _sp = sp; _log = log; }

    public static async Task<int> RunOnceAsync(Sql db, ILogger log, CancellationToken ct)
    {
        var rows = await db.QueryAsync("""
            SELECT "TenantSlug", "UserId", max("UserName"), "EntityType", "EntityId", max("OccurredAt")
            FROM audit_log
            WHERE "OccurredAt" > now() - interval '60 minutes' AND "Action" = ANY($1)
              AND "TenantSlug" IS NOT NULL AND "UserId" IS NOT NULL AND "UserId" <> 'system'
            GROUP BY "TenantSlug", "UserId", "EntityType", "EntityId"
            """, r => (Tenant: r.GetString(0), Ev: new AccessEvent(r.GetString(1), r.Str(2), MassViewDetector.Key(r.GetString(3), r.Str(4)),
                DateTime.SpecifyKind(r.GetFieldValue<DateTime>(5), DateTimeKind.Utc))), ct, (object)MassViewDetector.Actions); // dizi tek parametre ($1) olarak gitsin, params açılımı olmasın
        var now = DateTime.UtcNow;
        var total = 0;
        // Saklama: uyarı satırları (kullanıcı adı içerir) bir yıl tutulur; denetim kaydındaki karşılığı kalır.
        await db.ExecuteAsync("""DELETE FROM governance_security_alerts WHERE "DetectedAt" < now() - interval '365 days'""", ct);
        foreach (var tenant in rows.GroupBy(x => x.Tenant))
        {
            var settings = await SecuritySettingsStore.LoadAsync(db, tenant.Key, ct);
            var last = (await db.QueryAsync("""
                SELECT "UserId", max("DetectedAt") FROM governance_security_alerts
                WHERE "TenantSlug" = $1 AND "Kind" = 'MassView' AND "DetectedAt" > now() - interval '61 minutes' GROUP BY 1
                """, r => (U: r.GetString(0), At: DateTime.SpecifyKind(r.GetFieldValue<DateTime>(1), DateTimeKind.Utc)), ct, tenant.Key))
                .ToDictionary(x => x.U, x => x.At);
            var hits = MassViewDetector.Evaluate(tenant.Select(x => x.Ev), settings.MassViewThreshold, settings.MassViewWindowMinutes, now, last);
            foreach (var h in hits)
            {
                await RaiseAsync(db, tenant.Key, settings, h, ct);
                total++;
                log.LogWarning("Toplu görüntüleme uyarısı: kiracı={Tenant} kayıt={Count} pencere={Window} dk", tenant.Key, h.Distinct, settings.MassViewWindowMinutes);
            }
        }
        return total;
    }

    private static async Task RaiseAsync(Sql db, string tenant, SecuritySettings s, MassViewHit h, CancellationToken ct)
    {
        DateTime? blockedUntil = s.MassViewBlock ? DateTime.UtcNow.AddMinutes(SecuritySettings.BlockMinutes) : null;
        var id = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT INTO governance_security_alerts ("Id","TenantSlug","Kind","UserId","UserName","DistinctCount","WindowMinutes","Threshold","DetectedAt","BlockedUntil")
            VALUES ($1,$2,'MassView',$3,$4,$5,$6,$7,now(),$8)
            """, ct, id, tenant, h.UserId, h.UserName, h.Distinct, s.MassViewWindowMinutes, s.MassViewThreshold, blockedUntil);
        await ComplianceAudit.WriteAsync(db, tenant, "SecurityAlert", id.ToString(), "MassViewDetected",
            new { userId = h.UserId, userName = h.UserName, distinct = h.Distinct, windowMinutes = s.MassViewWindowMinutes, threshold = s.MassViewThreshold, blocked = blockedUntil is not null },
            "system", "Sistem (toplu görüntüleme dedektörü)", ct);
        Alerts.Inc();
        if (blockedUntil is not null) Blocks.Inc();
        var to = await SecuritySettingsStore.RecipientsAsync(db, tenant, s, ct);
        var who = string.IsNullOrWhiteSpace(h.UserName) ? "Bir kullanıcı" : h.UserName;
        await BulkNotifyLocalized.InAppAsync(db, tenant, to,
            "Güvenlik uyarısı: toplu kayıt görüntüleme", "Security alert: mass record viewing",
            $"{who} son {s.MassViewWindowMinutes} dakikada {h.Distinct} farklı çalışan kaydını açtı (eşik {s.MassViewThreshold})."
                + (blockedUntil is not null ? $" Hassas alan açması {SecuritySettings.BlockMinutes} dakika engellendi." : "")
                + " Veri koruma › Uyarılar ekranından inceleyin.",
            $"{who} opened {h.Distinct} different employee records in the last {s.MassViewWindowMinutes} minutes (threshold {s.MassViewThreshold})."
                + (blockedUntil is not null ? $" Revealing sensitive fields is blocked for {SecuritySettings.BlockMinutes} minutes." : "")
                + " Review it under Data protection › Alerts.",
            "security.massview", ct);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("governance/MassViewWorker", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<Sql>(), _log, ct);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // Göç uygulanmadan dağıtıldıysa sessiz geç (governance_security_alerts yok).
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Toplu görüntüleme dedektörü hata verdi");
            }
            await Task.Delay(Interval, ct);
        }
    }
}

/* ---------------------------------------------------------------- herkese açık form koruması */

public enum FormTokenStatus { Ok, Missing, Invalid, TooFast, Expired, Replayed }

/// <summary>
/// Herkese açık POST formları (etik bildirimi, iş başvurusu) için hafif bot koruması: form açılırken
/// GET ile HMAC imzalı zaman jetonu alınır; gönderimde imza, kapsam (kiracı/form), yaş (en az 3 sn,
/// en çok 2 saat) ve tek kullanım denetlenir. Üçüncü taraf CAPTCHA ve IP saklama yok. Anahtar
/// FORM_TOKEN_KEY (yoksa TENANT_SECRET_KEY) ortam değişkeninden türetilir; ikisi de yoksa süreç başına
/// rastgele (yeniden başlatmada açık formların jetonu geçersizleşir, kullanıcı yeniden dener).
/// Aynı sınıf recruitment-service'te de var (Services/FormGuard.cs).
/// </summary>
public sealed class FormGuard
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);
    public static readonly FormGuard Shared = new(DeriveKey());

    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new();

    public FormGuard(byte[] key) => _key = key;

    private static byte[] DeriveKey()
    {
        var src = Environment.GetEnvironmentVariable("FORM_TOKEN_KEY") is { Length: > 0 } k ? k
            : Environment.GetEnvironmentVariable("TENANT_SECRET_KEY") is { Length: > 0 } t ? t : null;
        return src is null ? RandomNumberGenerator.GetBytes(32)
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(src), Encoding.UTF8.GetBytes("hr360-form-guard-v1"));
    }

    private string Sign(string scope, long ts, string nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{scope}|{ts}|{nonce}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>"zaman.nonce.imza" biçiminde jeton.</summary>
    public string Issue(string scope, DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        return $"{ts}.{nonce}.{Sign(scope, ts, nonce)}";
    }

    public FormTokenStatus Verify(string? token, string scope, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return FormTokenStatus.Missing;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0].Length > 15 || parts[1].Length != 24 || !long.TryParse(parts[0], out var ts))
            return FormTokenStatus.Invalid;
        var expected = Encoding.ASCII.GetBytes(Sign(scope, ts, parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2]))) return FormTokenStatus.Invalid;
        var t = now ?? DateTimeOffset.UtcNow;
        var age = t - DateTimeOffset.FromUnixTimeMilliseconds(ts);
        if (age < MinAge) return FormTokenStatus.TooFast;
        if (age > MaxAge) return FormTokenStatus.Expired;
        if (_used.Count > 20_000)
            foreach (var kv in _used.Where(kv => t - kv.Value > MaxAge).ToList()) _used.TryRemove(kv.Key, out _);
        return _used.TryAdd(parts[1], t) ? FormTokenStatus.Ok : FormTokenStatus.Replayed;
    }

    /// <summary>Kullanıcıya gösterilecek ileti (Türkçe; istemci "@server:" anahtarıyla çevirir).</summary>
    public static string Message(FormTokenStatus s) => s switch
    {
        FormTokenStatus.TooFast => "Form çok hızlı gönderildi; birkaç saniye bekleyip yeniden deneyin.",
        FormTokenStatus.Expired => "Formun süresi doldu; sayfayı yenileyip yeniden gönderin.",
        _ => "Form doğrulanamadı; sayfayı yenileyip yeniden gönderin.",
    };
}
