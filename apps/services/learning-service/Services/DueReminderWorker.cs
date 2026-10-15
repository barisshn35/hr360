using Microsoft.EntityFrameworkCore;
using Npgsql;
using LearningService.Data;

namespace LearningService.Services;

/// <summary>
/// Dalga 11 (madde 84): son tarih hatırlatmaları — 30 gün, 7 gün kala ve son günde/gecikmede
/// (<see cref="ReminderPlan"/> aralıkları) çalışana ve departman başkanına uygulama içi bildirim.
///  * Training: son tarihi olan, tamamlanmamış eğitim atamaları (learning_enrollments."DueOn").
///  * Osh: İSG eğitimi geçerlilik bitişi (governance_osh_trainings, SALT OKUNUR; kişi + konu başına
///    en son eğitim — yenilenen eğitim için eski kayıt hatırlatılmaz). İSG eğitimi yasal zorunluluk
///    olduğundan yöneticiye de gider.
/// Gönderilenler learning_due_reminders'a yazılır (benzersiz anahtar), aynı hatırlatma ikinci kez gitmez;
/// son tarih değişirse yeni döngü başlar. Sertifika bitişleri <see cref="CertificateReminderWorker"/>'dadır.
/// Tablo henüz yoksa (migration uygulanmamış) tur sessizce atlanır.
/// </summary>
public sealed class DueReminderWorker : BackgroundService
{
    public const string Training = "Training";
    public const string Osh = "Osh";

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DueReminderWorker> _log;
    private readonly TimeSpan _interval;

    public DueReminderWorker(IServiceScopeFactory scopes, ILogger<DueReminderWorker> log)
    {
        _scopes = scopes;
        _log = log;
        var s = int.TryParse(Environment.GetEnvironmentVariable("CERT_REMINDER_SECONDS"), out var v) && v > 0 ? v : 3600;
        _interval = TimeSpan.FromSeconds(s);
    }

    public sealed class DueRow
    {
        public Guid SourceId { get; set; }
        public string TenantSlug { get; set; } = "";
        public Guid EmployeeId { get; set; }
        public string EmployeeName { get; set; } = "";
        public string Title { get; set; } = "";
        public DateOnly DueOn { get; set; }
        public bool Mandatory { get; set; }
        public Guid? HeadId { get; set; }
    }

    private const string PersonJoin = """
        JOIN employee_employees e ON e."Id" = {SUBJ} AND e."TenantSlug" = {TEN} AND e."Status" <> 'Terminated'
        LEFT JOIN LATERAL (
            SELECT x."DepartmentId" FROM employee_assignments x
            WHERE x."EmployeeId" = e."Id" AND x."EffectiveFrom" <= {0}
              AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= {0})
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, _interval.TotalSeconds)), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
            {
                _log.LogInformation("Son tarih hatırlatması atlandı: şema henüz hazır değil ({State})", ex.SqlState);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Son tarih hatırlatma turu başarısız"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LearningDbContext>();
        var today = TrTime.Today;

        var training = await db.Database.SqlQueryRaw<DueRow>("""
            SELECT en."Id" AS "SourceId", en."TenantSlug", en."EmployeeId", e."FirstName" || ' ' || e."LastName" AS "EmployeeName",
                   co."Title", en."DueOn", co."IsMandatory" AS "Mandatory", d."HeadEmployeeId" AS "HeadId"
            FROM learning_enrollments en
            JOIN learning_courses co ON co."Id" = en."CourseId" AND co."IsActive"
            """ + "\n" + PersonJoin.Replace("{SUBJ}", "en.\"EmployeeId\"").Replace("{TEN}", "en.\"TenantSlug\"") + "\n" + """
            WHERE en."DueOn" IS NOT NULL AND en."Status" IN ('Enrolled','InProgress','Failed')
              AND en."DueOn" BETWEEN {1} AND {2}
            """, today, today.AddDays(-30), today.AddDays(30)).ToListAsync(ct);

        List<DueRow> osh;
        try
        {
            osh = await db.Database.SqlQueryRaw<DueRow>("""
                SELECT t."Id" AS "SourceId", t."TenantSlug", t."EmployeeId", e."FirstName" || ' ' || e."LastName" AS "EmployeeName",
                       t."Topic" AS "Title", t."ExpiresOn" AS "DueOn", true AS "Mandatory", d."HeadEmployeeId" AS "HeadId"
                FROM (
                    SELECT DISTINCT ON (o."TenantSlug", p.pid, lower(trim(o."Topic")))
                           o."Id", o."TenantSlug", p.pid AS "EmployeeId", o."Topic", o."ExpiresOn"
                    FROM governance_osh_trainings o CROSS JOIN LATERAL unnest(o."ParticipantIds") AS p(pid)
                    WHERE o."ExpiresOn" IS NOT NULL
                    ORDER BY o."TenantSlug", p.pid, lower(trim(o."Topic")), o."ExpiresOn" DESC
                ) t
                """ + "\n" + PersonJoin.Replace("{SUBJ}", "t.\"EmployeeId\"").Replace("{TEN}", "t.\"TenantSlug\"") + "\n" + """
                WHERE t."ExpiresOn" BETWEEN {1} AND {2}
                """, today, today.AddDays(-30), today.AddDays(30)).ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { osh = new(); } // governance şeması yoksa

        var sent = 0;
        foreach (var r in training) sent += await RemindAsync(db, Training, r, today, ct);
        foreach (var r in osh) sent += await RemindAsync(db, Osh, r, today, ct);
        if (sent > 0) _log.LogInformation("Son tarih hatırlatması: {Count} bildirim", sent);
        return sent;
    }

    /// <summary>Bildirim metni (çalışan / yönetici) — saf, birim testli.</summary>
    public static (string Subject, string Body) Message(string source, string kind, bool manager, string title, string employeeName, DateOnly due, int days)
    {
        var date = due.ToString("dd.MM.yyyy");
        var what = source == Osh ? "İSG eğitimi" : "eğitim";
        var when = days > 0 ? $"{date} tarihinde ({days} gün sonra)" : days == 0 ? $"bugün ({date})" : $"{date} tarihinde";
        if (source == Osh)
        {
            if (manager)
                return kind == ReminderPlan.Expired && days < 0
                    ? ("Ekibinizde İSG eğitimi süresi doldu", $"{employeeName} adlı çalışanın «{title}» İSG eğitiminin geçerliliği {when} doldu.")
                    : ("Ekibinizde İSG eğitimi yenilemesi yaklaşıyor", $"{employeeName} adlı çalışanın «{title}» İSG eğitiminin geçerliliği {when} bitiyor.");
            return kind == ReminderPlan.Expired && days < 0
                ? ("İSG eğitiminizin süresi doldu", $"«{title}» İSG eğitiminizin geçerliliği {when} doldu. Yenileme için İSG uzmanıyla görüşün.")
                : ("İSG eğitiminizin süresi yaklaşıyor", $"«{title}» İSG eğitiminizin geçerliliği {when} bitiyor.");
        }
        if (manager)
            return kind == ReminderPlan.Expired && days < 0
                ? ("Ekibinizde zorunlu eğitim gecikti", $"{employeeName} adlı çalışanın «{title}» {what} için son tarih {when} geçti.")
                : ("Ekibinizde zorunlu eğitimin son tarihi yaklaşıyor", $"{employeeName} adlı çalışanın «{title}» {what} için son tarih {when}.");
        return kind == ReminderPlan.Expired && days < 0
            ? ("Eğitiminizin son tarihi geçti", $"«{title}» eğitimini tamamlamanız için son tarih {when} geçti.")
            : ("Eğitiminizin son tarihi yaklaşıyor", $"«{title}» eğitimini tamamlamanız için son tarih {when}.");
    }

    private async Task<int> RemindAsync(LearningDbContext db, string source, DueRow r, DateOnly today, CancellationToken ct)
    {
        var days = r.DueOn.DayNumber - today.DayNumber;
        var kind = ReminderPlan.KindFor(days);
        if (kind is null) return 0;
        var (s, b) = Message(source, kind, false, r.Title, r.EmployeeName, r.DueOn, days);
        var n = await SendOnceAsync(db, source, r, kind, r.EmployeeId, s, b, "/panel/egitim-takibi", ct);
        // Yönetici: zorunlu eğitimlerde (İSG her zaman zorunludur) departman başkanı.
        if (r.Mandatory && r.HeadId is { } head && head != r.EmployeeId)
        {
            var (ms, mb) = Message(source, kind, true, r.Title, r.EmployeeName, r.DueOn, days);
            n += await SendOnceAsync(db, source, r, kind, head, ms, mb, "/panel/egitim-takibi?gorunum=ekip", ct);
        }
        return n;
    }

    /// <summary>Kayıt + bildirim tek işlemde; benzersiz anahtar çakışırsa (başka tur/örnek gönderdi) hiçbir şey yazılmaz.
    /// Kaynak bu arada silindiyse (FK yok; yalnızca hatırlatma kaydı) yine tutarlıdır.</summary>
    private async Task<int> SendOnceAsync(LearningDbContext db, string source, DueRow r, string kind, Guid recipient,
        string subject, string body, string url, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var inserted = await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO learning_due_reminders ("Id","TenantSlug","SourceType","SourceId","SubjectEmployeeId","Kind","RecipientEmployeeId","DueOn","SentAt")
                VALUES ({0},{1},{2},{3},{4},{5},{6},{7},now()) ON CONFLICT DO NOTHING
                """, Guid.NewGuid(), r.TenantSlug, source, r.SourceId, r.EmployeeId, kind, recipient, r.DueOn);
            if (inserted == 0) { await tx.RollbackAsync(ct); return 0; }
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt","ActionUrl")
                VALUES ({0},{1},{2},NULL,'InApp',{3},{4},{5},'Pending',0,now(),{6})
                """, Guid.NewGuid(), r.TenantSlug, recipient, $"learning.due.{source.ToLowerInvariant()}.{kind.ToLowerInvariant()}", subject, body, url);
            await tx.CommitAsync(ct);
            return 1;
        }
        catch (PostgresException ex) when (ex.SqlState is "23503" or "23505")
        {
            _log.LogDebug("Hatırlatma atlandı ({State})", ex.SqlState); // eşzamanlı tur ya da kayıt silindi
            return 0;
        }
    }
}
