using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Models;

namespace GovernanceService.Controllers;

/* ======================================================================
 * KVKK araçları: açık rıza/aydınlatma kayıtları, ilgili kişi başvuruları
 * (KVKK m.11 — 30 gün içinde yanıt), kişisel veri dışa aktarımı,
 * ayrılmış çalışanın anonimleştirilmesi ve saklama süresi politikaları.
 * ==================================================================== */
[Route("api/privacy")]
[Authorize]
public class PrivacyController : AppController
{
    public sealed record ConsentType(string Type, string Title, string Version, bool Required, string Text);

    public static readonly ConsentType[] ConsentTypes =
    {
        new("KVKK_AYDINLATMA", "Aydınlatma metni (KVKK m.10)", "2026.1", true,
            "Kişisel verileriniz; iş sözleşmesinin kurulması ve ifası, yasal yükümlülükler (SGK, vergi, İSG) ve meşru menfaat kapsamında, İK süreçlerinin yürütülmesi amacıyla işlenir. Haklarınız için İK ile iletişime geçebilir veya bu ekrandan başvuru yapabilirsiniz."),
        new("ACIK_RIZA_SAGLIK", "Sağlık verilerinin işlenmesi", "2026.1", false,
            "Rapor, iş kazası ve periyodik muayene kayıtlarınızın İK ve işyeri hekimi tarafından işlenmesine açık rıza veriyorum."),
        new("ACIK_RIZA_YURTDISI", "Yurt dışına veri aktarımı", "2026.1", false,
            "Grup şirketleriyle ortak kullanılan sistemler nedeniyle kişisel verilerimin yurt dışına aktarılmasına açık rıza veriyorum."),
        new("ILETISIM_IZNI", "Etkinlik ve duyuru iletişimi", "2026.1", false,
            "Şirket içi etkinlik, anket ve duyuruların e-posta/SMS ile tarafıma iletilmesini kabul ediyorum."),
        new("GORSEL_KULLANIM", "Fotoğraf ve ad kullanımı", "2026.1", false,
            "Şirket içi iletişimde (intranet, bülten) fotoğrafımın ve adımın kullanılmasına izin veriyorum."),
    };

    private readonly GovernanceDbContext _db;
    public PrivacyController(GovernanceDbContext db) => _db = db;

    [HttpGet("consent-types")]
    public async Task<IActionResult> Types(CancellationToken ct) => Ok(await ConsentCatalog.EffectiveAsync(_db, ct));

    [HttpGet("consents/me")]
    public async Task<IActionResult> MyConsents(CancellationToken ct)
    {
        var mine = await _db.Consents.AsNoTracking().Where(c => c.UserId == Me.UserId).OrderByDescending(c => c.RecordedAt).ToListAsync(ct);
        var types = await ConsentCatalog.EffectiveAsync(_db, ct);
        return Ok(types.Select(t =>
        {
            var last = mine.FirstOrDefault(c => c.ConsentType == t.Type);
            return new { t.Type, t.Title, t.Version, t.Required, t.Text, granted = last?.Granted, recordedAt = last?.RecordedAt,
                outdated = last is not null && last.Version != t.Version, history = mine.Where(c => c.ConsentType == t.Type).Take(5).Select(c => new { c.Granted, c.Version, c.RecordedAt }) };
        }));
    }

    public record ConsentInput(string ConsentType, bool Granted);

    [HttpPost("consents/me")]
    public async Task<IActionResult> Record(ConsentInput body, CancellationToken ct)
    {
        // Platform yöneticisi (süreli erişim izniyle bile) şirketin çalışanı değildir: onun adına
        // aydınlatma/rıza kaydı tutulmaz (yanlışlıkla şirketin rıza kayıtlarına karışmasın).
        if (Me.IsPlatformAdmin)
            return StatusCode(403, new { message = "Platform yöneticisi adına onay kaydı tutulmaz.", code = "platform_admin_consent" });
        var t = (await ConsentCatalog.EffectiveAsync(_db, ct)).FirstOrDefault(x => x.Type == body.ConsentType);
        if (t is null) return BadRequest(new { message = "Bilinmeyen onay tipi." });
        if (t.Required && !body.Granted) return BadRequest(new { message = "Aydınlatma metni okundu olarak işaretlenmelidir (bu bir rıza değil, bilgilendirmedir)." });
        var me = await MyPersonAsync(ct);
        _db.Consents.Add(new Consent
        {
            UserId = Me.UserId, EmployeeId = me?.Id, PersonName = me?.Name ?? Me.Name, ConsentType = t.Type, Version = t.Version,
            Granted = body.Granted, IpAddress = Request.Headers["X-Real-IP"].FirstOrDefault() ?? HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpGet("consents")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ConsentSummary(CancellationToken ct)
    {
        var people = await People.ListAsync(Tenant, ct);
        var all = await _db.Consents.AsNoTracking().ToListAsync(ct);
        var latest = all.GroupBy(c => (c.UserId, c.ConsentType)).Select(g => g.OrderByDescending(c => c.RecordedAt).First()).ToList();
        var effective = await ConsentCatalog.EffectiveAsync(_db, ct);
        return Ok(new
        {
            population = people.Count,
            types = effective.Select(t => new
            {
                t.Type, t.Title, t.Required, t.Version,
                granted = latest.Count(c => c.ConsentType == t.Type && c.Granted),
                denied = latest.Count(c => c.ConsentType == t.Type && !c.Granted),
                pending = Math.Max(0, people.Count - latest.Count(c => c.ConsentType == t.Type)),
                outdated = latest.Count(c => c.ConsentType == t.Type && c.Version != t.Version),
            }),
            missingRequired = people.Where(p => p.UserId is not null && !latest.Any(c => c.UserId == p.UserId && c.ConsentType == "KVKK_AYDINLATMA"))
                .Select(p => new { employeeId = p.Id, name = p.Name, department = p.Department }),
        });
    }

    /* ---------------------------------------------------- ilgili kişi başvurusu */

    public record RequestInput(string Kind, string? Details);

    [HttpPost("requests")]
    public async Task<IActionResult> CreateRequest(RequestInput body, CancellationToken ct)
    {
        if (body.Kind is not ("Access" or "Rectification" or "Erasure" or "Objection"))
            return BadRequest(new { message = "Geçersiz başvuru türü." });
        var me = await MyPersonAsync(ct);
        if (body.Details is { Length: > 4000 }) return BadRequest(new { message = L("Açıklama en fazla 4000 karakter.", "Details must be at most 4000 characters.") });
        // İK'nın başvuruyu karşılayabilmesi için talep açıklanmalı (KVKK m.13: başvuru talebi içermeli).
        if ((body.Details?.Trim().Length ?? 0) < 10)
            return BadRequest(new { message = L("Başvurunuzu en az 10 karakterle açıklayın.", "Please describe your request in at least 10 characters.") });
        // Kendi oturumundan gelen başvuruda kimlik oturumla doğrulanmıştır.
        var r = new DataRequest { UserId = Me.UserId, EmployeeId = me?.Id, PersonName = me?.Name ?? Me.Name, Kind = body.Kind, Details = body.Details?.Trim(),
            Channel = "Panel", IdentityVerified = true, VerificationMethod = "Session", VerifiedAt = DateTime.UtcNow, VerifiedBy = "HR360" };
        _db.DataRequests.Add(r);
        await _db.SaveChangesAsync(ct);
        // Dalga 10: oturumla doğrulanmış erişim başvurusunda veri paketi hemen hazırlanır; başvurucu, İK
        // başvuruyu sonuçlandırınca indirir. Paket hazırlanamazsa (göç yok, çalışan kaydı yok) başvuru yine alınır.
        if (r.Kind == "Access" && r.EmployeeId is not null)
        {
            try { await DataRequestPackages.CreateAsync(Db, Tenant, r.Id, People, "system", "Sistem (otomatik veri paketi)", ct); }
            catch (Exception ex) when (ex is Npgsql.PostgresException or InvalidOperationException) { }
        }
        return Ok(r);
    }

    [HttpGet("requests")]
    public async Task<IActionResult> Requests(CancellationToken ct)
    {
        var q = _db.DataRequests.AsNoTracking();
        if (!Me.IsHr) q = q.Where(r => r.UserId == Me.UserId);
        var rows = await q.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        // Dalga 10: süre takibi (20./27. gün hatırlatması, süre aşımı uyarısı) ve veri paketi durumu.
        var extra = new Dictionary<Guid, (DateTime? R20, DateTime? R27, DateTime? Over)>();
        var packages = new Dictionary<Guid, (DateTime Created, DateTime Expires, int Size, int Downloads, string Sha)>();
        try
        {
            foreach (var x in await Db.QueryAsync("""SELECT "Id","Reminder20At","Reminder27At","OverdueAlertAt" FROM governance_data_requests WHERE "TenantSlug" = $1""",
                         r => (r.GetGuid(0), r.Ts(1), r.Ts(2), r.Ts(3)), ct, Tenant))
                extra[x.Item1] = (x.Item2, x.Item3, x.Item4);
            foreach (var x in await Db.QueryAsync("""
                         SELECT DISTINCT ON ("RequestId") "RequestId","CreatedAt","ExpiresAt","SizeBytes","DownloadCount","Sha256"
                         FROM governance_data_request_packages WHERE "TenantSlug" = $1 AND "ExpiresAt" > now() ORDER BY "RequestId","CreatedAt" DESC
                         """, r => (r.GetGuid(0), r.GetFieldValue<DateTime>(1), r.GetFieldValue<DateTime>(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5)), ct, Tenant))
                packages[x.Item1] = (x.Item2, x.Item3, x.Item4, x.Item5, x.Item6);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState is "42P01" or "42703") { }
        var now = DateTime.UtcNow;
        return Ok(rows.Select(r =>
        {
            var open = r.Status is "Received" or "InProgress";
            var ex = extra.GetValueOrDefault(r.Id);
            var pkg = packages.TryGetValue(r.Id, out var pk) ? pk : default;
            var hasPkg = packages.ContainsKey(r.Id);
            return new
            {
                r.Id, r.PersonName, r.EmployeeId, r.Kind, r.Details, r.Status, r.Response, r.DueAt, r.CreatedAt, r.CompletedAt,
                r.Channel, contact = Me.IsHr ? r.Contact : null, r.IdentityVerified, r.VerificationMethod, r.VerifiedBy, r.VerifiedAt,
                overdue = open && r.DueAt < now, daysLeft = (int)Math.Ceiling((r.DueAt - now).TotalDays),
                day = open ? DataRequestReminders.DayOf(r.CreatedAt, now) : (int?)null, legalDays = DataRequestReminders.LegalDays,
                reminder20At = ex.R20, reminder27At = ex.R27, overdueAlertAt = ex.Over,
                package = !hasPkg ? null : new
                {
                    createdAt = pkg.Created, expiresAt = pkg.Expires, sizeBytes = pkg.Size, downloads = pkg.Downloads, sha256 = Me.IsHr ? pkg.Sha : null,
                    // Başvurucu paketi başvuru sonuçlanınca indirir; İK her zaman.
                    downloadable = Me.IsHr || (r.Status == "Completed" && r.IdentityVerified),
                },
            };
        }));
    }

    /// <summary>
    /// Başvurucu, henüz sonuçlanmamış (Received/InProgress) kendi başvurusunu geri çekebilir.
    /// Kayıt silinmez ("Withdrawn"); hesap verebilirlik için denetim kaydına yazılır.
    /// </summary>
    [HttpPost("requests/{id:guid}/withdraw")]
    public async Task<IActionResult> WithdrawRequest(Guid id, CancellationToken ct)
    {
        var r = await _db.DataRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == Me.UserId, ct);
        if (r is null) return NotFound();
        // Koşullu güncelleme: İK aynı anda sonuçlandırırsa yalnızca biri kazanır.
        if (!DataRequestStates.CanTransition(r.Status, "Withdrawn", byApplicant: true)
            || await DataRequestStates.TryMoveAsync(Db, Tenant, r.Id, r.Status, "Withdrawn", null, ct) == 0)
            return Conflict(new { message = L("Yanıtlanmış başvuru geri çekilemez.", "A request that has been answered cannot be withdrawn."), code = "request_closed" });
        r.Status = "Withdrawn";
        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
            VALUES ($1,'governance-service','DataRequest',$2,'Withdrawn',$3::jsonb,$4,$5,now())
            """, ct, Tenant, r.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { r.Kind }), Me.UserId, Me.Name);
        return Ok(new { r.Id, r.Status });
    }

    public record RequestUpdate(string Status, string? Response);

    [HttpPatch("requests/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateRequest(Guid id, RequestUpdate body, CancellationToken ct)
    {
        var r = await _db.DataRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (body.Status is not ("Received" or "InProgress" or "Completed" or "Rejected"))
            return BadRequest(new { message = L("Geçersiz durum.", "Invalid status.") });
        // Durum makinesi: sonuçlanmış/geri çekilmiş başvuru değiştirilemez (yanıt bildirimi tekrar gitmez).
        if (!DataRequestStates.CanTransition(r.Status, body.Status))
            return Conflict(new
            {
                message = L("Bu başvuru sonuçlanmış ya da geri çekilmiş; durumu değiştirilemez. Yeni talep için yeni başvuru açılmalıdır.",
                    "This request has been closed or withdrawn; its status cannot be changed. A new request must be opened instead."),
                code = "invalid_transition", from = r.Status, to = body.Status,
            });
        if (body.Status is "Completed" or "Rejected" && string.IsNullOrWhiteSpace(body.Response))
            return BadRequest(new { message = L("Sonuçlandırırken başvurucuya yanıt yazılmalı (KVKK m.13).", "A response to the applicant is required when closing the request (KVKK art. 13).") });
        if (body.Response is { Length: > 8000 })
            return BadRequest(new { message = L("Yanıt en fazla 8000 karakter.", "The response must be at most 8000 characters.") });
        if (body.Status == "Completed" && !r.IdentityVerified)
            return BadRequest(new { message = L("Başvurucunun kimliği doğrulanmadan başvuru karşılanamaz (kişisel veri yanlış kişiye verilmemeli).", "The request cannot be fulfilled before the applicant's identity is verified."), code = "identity_unverified" });
        var from = r.Status;
        var response = string.IsNullOrWhiteSpace(body.Response) ? null : body.Response.Trim();
        // Koşullu geçiş: eşzamanlı iki "yanıtla" isteğinden yalnızca biri uygulanır ve bildirim gönderir.
        if (await DataRequestStates.TryMoveAsync(Db, Tenant, r.Id, from, body.Status, response, ct) == 0)
            return Conflict(new { message = L("Başvurunun durumu bu arada değişti; sayfayı yenileyin.", "The request status changed in the meantime; refresh the page."), code = "status_changed" });
        r.Status = body.Status;
        r.Response = response ?? r.Response;
        r.CompletedAt = DataRequestStates.IsTerminal(body.Status) ? DateTime.UtcNow : null;
        if (from != body.Status)
            await ComplianceAudit.WriteAsync(Db, Tenant, "DataRequest", r.Id.ToString(), "StatusChanged", new { from, to = body.Status }, Me.UserId, Me.Name, ct);
        if (DataRequestStates.NotifiesApplicant(from, body.Status) && r.EmployeeId is { } emp && r.Channel == "Panel")
        {
            // Dalga 10: veri paketi, sonuçlandırmadan sonra en az 30 gün indirilebilir kalır.
            try
            {
                await Db.ExecuteAsync("""
                    UPDATE governance_data_request_packages SET "ExpiresAt" = greatest("ExpiresAt", now() + interval '30 days')
                    WHERE "TenantSlug" = $1 AND "RequestId" = $2
                    """, ct, Tenant, r.Id);
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == "42P01") { }
            await HttpContext.RequestServices.GetRequiredService<Notifier>().LocalizedAsync(Tenant, emp,
                "KVKK başvurunuz yanıtlandı", "Your data subject request has been answered",
                "Yanıtı Profilim › Gizlilik (KVKK) ekranında görebilirsiniz." + (r.Kind == "Access" && body.Status == "Completed" ? " Veri paketiniz de oradan indirilebilir." : ""),
                "You can read the answer under My profile › Privacy (KVKK)." + (r.Kind == "Access" && body.Status == "Completed" ? " Your data package can be downloaded there as well." : ""),
                "privacy.request.answered", ct);
        }
        return Ok(r);
    }

    /* ---------------------------------------------------------- veri dışa aktarımı */

    /// <summary>Bir çalışanın tüm modüllerdeki kişisel verisi (JSON). Sahibi veya İK.</summary>
    [HttpGet("export/{employeeId:guid}")]
    public async Task<IActionResult> Export(Guid employeeId, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me?.Id != employeeId && !Me.IsHr) return Forbid();
        var person = await People.FindAsync(Tenant, employeeId, ct);
        if (person is null) return NotFound();
        // Dalga 10: döküm içeriği erişim başvurusu veri paketiyle ortak (PersonalDataExport).
        var bundle = await PersonalDataExport.BuildAsync(Db, Tenant, person, includeCompensation: Me.IsHr || me?.Id == employeeId, ct);
        // Erişim kayıtları (veri paketinde ayrı dosya): bu dışa aktarımın kendisi eklenmeden önce okunur.
        bundle[PersonalDataExport.AccessLogKey] = await PersonalDataExport.AccessLogAsync(Db, Tenant, employeeId, ct);
        bundle["_icerik"] = PersonalDataExport.Contents(bundle);

        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","CorrelationId","OccurredAt")
            VALUES ($1,'governance-service','PersonalDataExport',$2,'Exported','{}'::jsonb,$3,$4,$5,now())
            """, ct, Tenant, employeeId.ToString(), Me.UserId, Me.Name, Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier);

        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return File(json, "application/json", $"kisisel-veri-{person.Name.Replace(' ', '-').ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    [HttpPost("anonymize/{employeeId:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Anonymize(Guid employeeId, CancellationToken ct)
    {
        var run = await Infrastructure.Retention.AnonymizeEmployeesDetailedAsync(Db, Tenant, employeeId, 0, ct);
        var n = run.Affected;
        if (n == 0) return BadRequest(new { message = L("Yalnızca işten ayrılmış (Terminated) çalışanlar anonimleştirilebilir.", "Only terminated employees can be anonymized.") });
        var log = Infrastructure.Retention.Log(_db, Tenant, "TerminatedEmployees", "Anonymize", n, 0, "Manual", Me.Name);
        await _db.SaveChangesAsync(ct);
        await Infrastructure.Retention.SaveDetailsAsync(Db, log, run, ct);
        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
            VALUES ($1,'governance-service','Employee',$2,'Anonymized','{}'::jsonb,$3,$4,now())
            """, ct, Tenant, employeeId.ToString(), Me.UserId, Me.Name);
        return Ok(new { anonymized = n, tables = run.Steps.Where(s => s.Rows > 0).Select(s => new { s.Table, s.Rows }) });
    }

    /* ---------------------------------------------------------- saklama politikaları */

    [HttpGet("retention")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Retention(CancellationToken ct)
    {
        var existing = await _db.RetentionPolicies.ToListAsync(ct);
        foreach (var (cat, meta) in Infrastructure.Retention.Categories)
            if (existing.All(p => p.Category != cat))
                _db.RetentionPolicies.Add(new RetentionPolicy { Category = cat, RetentionMonths = meta.DefaultMonths, Action = meta.Actions[0], IsEnabled = false });
        await _db.SaveChangesAsync(ct);
        var all = await _db.RetentionPolicies.AsNoTracking().OrderBy(p => p.Category).ToListAsync(ct);
        return Ok(all.Select(p => new
        {
            p.Id, p.Category, label = Infrastructure.Retention.LabelOf(p.Category, En),
            allowedActions = Infrastructure.Retention.Categories.GetValueOrDefault(p.Category).Actions,
            p.RetentionMonths, p.Action, p.IsEnabled, p.LastRunAt, p.LastAffected,
        }));
    }

    public record RetentionInput(int RetentionMonths, string Action, bool IsEnabled);

    [HttpPut("retention/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateRetention(Guid id, RetentionInput body, CancellationToken ct)
    {
        var p = await _db.RetentionPolicies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var meta = Infrastructure.Retention.Categories.GetValueOrDefault(p.Category);
        if (meta.Actions is null || !meta.Actions.Contains(body.Action)) return BadRequest(new { message = "Bu kategori için geçersiz işlem." });
        if (body.RetentionMonths is < 1 or > 240) return BadRequest(new { message = "Süre 1–240 ay olmalı." });
        p.RetentionMonths = body.RetentionMonths;
        p.Action = body.Action;
        p.IsEnabled = body.IsEnabled;
        await _db.SaveChangesAsync(ct);
        return Ok(p);
    }

    [HttpPost("retention/{id:guid}/run")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> RunRetention(Guid id, CancellationToken ct)
    {
        var p = await _db.RetentionPolicies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var run = await Infrastructure.Retention.RunDetailedAsync(Db, p, ct);
        p.LastAffected = run.Affected;
        p.LastRunAt = DateTime.UtcNow;
        var log = Infrastructure.Retention.Log(_db, Tenant, p.Category, p.Action, p.LastAffected, p.RetentionMonths, "Manual", Me.Name);
        await _db.SaveChangesAsync(ct);
        await Infrastructure.Retention.SaveDetailsAsync(Db, log, run, ct);
        // Nesne deposu dosyaları hemen işlenir (bakım turunu beklemeden); ulaşılamazsa tur yeniden dener.
        if (run.Queued > 0)
            await StorageDeletions.ProcessAsync(Db, HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>(), Tenant, ct);
        return Ok(new { affected = p.LastAffected, tables = run.Steps.Select(s => new { s.Table, s.Label, s.Kind, s.Rows }), storageQueued = run.Queued });
    }

    /// <summary>
    /// Dalga 10 (madde 56): kuru çalıştırma. Politikanın KAYITLI ayarıyla (süre, işlem) hangi tablodan kaç
    /// satırın etkileneceğini ve hangi dosya anahtarlarının silineceğini gösterir; hiçbir şey değiştirmez.
    /// </summary>
    [HttpGet("retention/{id:guid}/preview")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PreviewRetention(Guid id, CancellationToken ct)
    {
        var p = await _db.RetentionPolicies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var plan = await RetentionPlans.BuildAsync(Db, Tenant, p.Category, p.Action, p.RetentionMonths, ct);
        var steps = await RetentionPlans.PreviewAsync(Db, plan, ct);
        var storage = new List<object>();
        foreach (var s in plan.Storage)
        {
            var n = 0;
            try { n = (await Db.QueryAsync(s.Select, r => r.GetString(0), ct, plan.Args)).Count; }
            catch (Npgsql.PostgresException e) when (e.SqlState is "42P01" or "42703") { }
            storage.Add(new { table = s.Table, column = s.Column, keys = n });
        }
        return Ok(new
        {
            p.Category, label = Infrastructure.Retention.LabelOf(p.Category, En), p.Action, p.RetentionMonths, p.IsEnabled,
            subjects = plan.Subjects < 0 ? (int?)null : plan.Subjects,
            total = steps.Sum(s => s.Rows),
            tables = steps.Select(s => new { s.Table, s.Label, s.Kind, s.Rows, s.Skipped }),
            storage,
            retained = plan.Retained.Select(r => new { r.Table, r.Reason }),
        });
    }
}

/* ======================================================================
 * Belge şablonları + toplu üretim. Şablon {{calisan.adSoyad}} gibi yer
 * tutucular içerir; render ucu seçilen her çalışan için doldurulmuş HTML
 * döner, arayüz bunları sayfa sonlarıyla birleştirip PDF olarak yazdırır.
 * ==================================================================== */
[Route("api/documents/templates")]
[Authorize(Policy = "RequireHrAdmin")]
public class DocumentTemplatesController : AppController
{
    public static readonly (string Key, string Label)[] Placeholders =
    {
        ("calisan.adSoyad", "Ad soyad"), ("calisan.ad", "Ad"), ("calisan.soyad", "Soyad"), ("calisan.eposta", "E-posta"),
        ("calisan.telefon", "Telefon"), ("calisan.pozisyon", "Pozisyon"), ("calisan.departman", "Departman"),
        ("calisan.iseGiris", "İşe giriş tarihi"), ("calisan.kidemYil", "Kıdem (yıl)"), ("calisan.yoneticisi", "Departman yöneticisi"),
        ("izin.kalanYillik", "Kalan yıllık izin"), ("ucret.brut", "Güncel brüt ücret"),
        ("sirket.ad", "Şirket adı"), ("sirket.vergiNo", "Vergi no"), ("bugun", "Bugünün tarihi"), ("belge.no", "Belge numarası"),
    };

    private readonly GovernanceDbContext _db;
    public DocumentTemplatesController(GovernanceDbContext db) => _db = db;

    [HttpGet("placeholders")]
    public IActionResult GetPlaceholders() => Ok(Placeholders.Select(p => new { key = p.Key, label = p.Label }));

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _db.DocTemplates.AsNoTracking().OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct));

    public record TemplateInput(string Name, string Category, string Body, bool? SelfService = null, bool? RequiresApproval = null);

    [HttpPost]
    public async Task<IActionResult> Create(TemplateInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Body)) return BadRequest(new { message = "Ad ve içerik zorunlu." });
        var t = new DocTemplate { Name = body.Name.Trim(), Category = body.Category?.Trim() is { Length: > 0 } c ? c : "Genel", Body = Sanitize(body.Body),
            SelfService = body.SelfService ?? false, RequiresApproval = body.RequiresApproval ?? true };
        _db.DocTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, TemplateInput body, CancellationToken ct)
    {
        var t = await _db.DocTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        t.Name = body.Name.Trim();
        t.Category = body.Category?.Trim() is { Length: > 0 } c ? c : t.Category;
        t.Body = Sanitize(body.Body);
        if (body.SelfService is { } ss) t.SelfService = ss;
        if (body.RequiresApproval is { } ra) t.RequiresApproval = ra;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await _db.DocTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        _db.DocTemplates.Remove(t);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("samples")]
    public async Task<IActionResult> Samples(CancellationToken ct)
    {
        var have = await _db.DocTemplates.Select(t => t.Name).ToListAsync(ct);
        var samples = new (string Name, string Cat, string Body)[]
        {
            ("Çalışma belgesi", "Resmî yazılar", """
                <h2 style="text-align:center">ÇALIŞMA BELGESİ</h2>
                <p>Şirketimiz <b>{{sirket.ad}}</b> bünyesinde <b>{{calisan.adSoyad}}</b>, <b>{{calisan.iseGiris}}</b> tarihinden bu yana
                <b>{{calisan.departman}}</b> biriminde <b>{{calisan.pozisyon}}</b> olarak çalışmaktadır.</p>
                <p>Bu belge, ilgilinin talebi üzerine 4857 sayılı İş Kanunu'nun 28. maddesi kapsamında düzenlenmiştir.</p>
                <p style="margin-top:48px">{{bugun}}<br/>Belge no: {{belge.no}}</p><p style="text-align:right">İnsan Kaynakları<br/>{{sirket.ad}}</p>
                """),
            ("Görev değişikliği bildirimi", "Çalışan yazışmaları", """
                <p>Sayın {{calisan.adSoyad}},</p>
                <p>{{bugun}} itibarıyla <b>{{calisan.departman}}</b> biriminde <b>{{calisan.pozisyon}}</b> görevinde çalışmaya devam edeceğinizi bildiririz.
                Yöneticiniz: {{calisan.yoneticisi}}.</p><p>Yeni görevinizde başarılar dileriz.</p><p>{{sirket.ad}} — İnsan Kaynakları</p>
                """),
            ("Yıllık izin hatırlatması", "Çalışan yazışmaları", """
                <p>Merhaba {{calisan.ad}},</p><p>{{bugun}} itibarıyla <b>{{izin.kalanYillik}} gün</b> kullanılmamış yıllık izniniz bulunmaktadır.
                Dinlenme hakkınızı yıl içine yaymanızı öneririz.</p><p>İK ekibi</p>
                """),
            ("Maaş yazısı", "Resmî yazılar", """
                <h2 style="text-align:center">MAAŞ YAZISI</h2>
                <p>Şirketimiz <b>{{sirket.ad}}</b> çalışanı <b>{{calisan.adSoyad}}</b>, <b>{{calisan.iseGiris}}</b> tarihinden bu yana
                <b>{{calisan.pozisyon}}</b> olarak çalışmakta olup güncel aylık brüt ücreti <b>{{ucret.brut}}</b>'dir.</p>
                <p>Bu yazı, ilgilinin talebi üzerine ve yalnızca belirtilen amaçla kullanılmak üzere düzenlenmiştir.</p>
                <p style="margin-top:48px">{{bugun}}<br/>Belge no: {{belge.no}}</p><p style="text-align:right">İnsan Kaynakları<br/>{{sirket.ad}}</p>
                """),
            // Bordro dalgası 8 (madde 61): kıdem/ihbar hesabından ibraname. Kıdem/ihbar yer tutucularını
            // compensation-service doldurur (Bordro › Kıdem ve ihbar › onaylı hesap › İbraname); burada
            // yalnızca metin düzenlenir. Hukuk birimince uyarlanmalıdır (TBK m.420).
            ("İbraname", "Gizli", """
                <h2 style="text-align:center">İBRANAME</h2>
                <p><b>{{sirket.ad}}</b> nezdinde <b>{{calisan.iseGiris}}</b> – <b>{{ayrilis.tarih}}</b> tarihleri arasında
                <b>{{calisan.pozisyon}}</b> olarak çalıştım. İş sözleşmem <b>{{ayrilis.neden}}</b> nedeniyle sona ermiştir.</p>
                <p>Hizmet süresi: {{kidem.gun}} gün ({{kidem.yil}} yıl). Giydirilmiş aylık brüt ücret: {{ucret.giydirilmis}}.</p>
                <p>Kıdem tazminatı: brüt {{kidem.brut}}, damga vergisi {{kidem.damga}}, net {{kidem.net}}.<br/>
                İhbar tazminatı ({{ihbar.hafta}} hafta): brüt {{ihbar.brut}}, gelir vergisi {{ihbar.gv}}, damga vergisi {{ihbar.damga}}, net {{ihbar.net}}.<br/>
                Kullanılmayan yıllık izin ({{izin.gun}} gün): brüt {{izin.brut}}.<br/>
                <b>Toplam net (kıdem + ihbar): {{toplam.net}}</b></p>
                <p>Yukarıdaki tutarların banka aracılığıyla tarafıma ödendiğini beyan ederim.</p>
                <p style="margin-top:48px">{{bugun}}</p><p style="text-align:right">{{calisan.adSoyad}}<br/>İmza</p>
                """),
            ("Ücret bilgilendirme yazısı", "Gizli", """
                <p>Sayın {{calisan.adSoyad}},</p><p>{{bugun}} itibarıyla güncel aylık brüt ücretiniz <b>{{ucret.brut}}</b> olarak belirlenmiştir.</p>
                <p>Bu yazı kişiye özeldir.</p><p>{{sirket.ad}}</p>
                """),
        };
        var added = 0;
        foreach (var s in samples.Where(s => !have.Contains(s.Name)))
        {
            // Çalışma belgesi onaysız, maaş yazısı İK onayıyla çalışanın kendisince talep edilebilir.
            _db.DocTemplates.Add(new DocTemplate { Name = s.Name, Category = s.Cat, Body = s.Body.Trim(),
                SelfService = s.Name is "Çalışma belgesi" or "Maaş yazısı", RequiresApproval = s.Name != "Çalışma belgesi" });
            added++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { added });
    }

    public record RenderInput(List<Guid> EmployeeIds);

    [HttpPost("{id:guid}/render")]
    public async Task<IActionResult> Render(Guid id, RenderInput body, CancellationToken ct)
    {
        var t = await _db.DocTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        if (body.EmployeeIds.Count is 0 or > 500) return BadRequest(new { message = "1–500 çalışan seçin." });
        var canSeePay = Me.IsHr || Me.Roles.Contains("ext-compensation-view");
        var docs = await DocRenderer.RenderAsync(Db, People, Tenant, t.Body, body.EmployeeIds, canSeePay, ct);
        return Ok(new { template = t.Name, documents = docs.Select(d => new { employeeId = d.EmployeeId, name = d.Name, html = d.Html }) });
    }

    /// <summary>Şablon HTML'inde betik ve olay öznitelikleri kabul edilmez.</summary>
    public static string Sanitize(string html)
    {
        var s = Regex.Replace(html, @"<\s*(script|iframe|object|embed|style)[^>]*>.*?<\s*/\s*\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<\s*(script|iframe|object|embed)[^>]*/?>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"javascript\s*:", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }
}

/// <summary>Belge şablonu doldurma (toplu üretim ve çalışan belge talebi ortak kullanır).</summary>
public static class DocRenderer
{
    public static async Task<List<(Guid EmployeeId, string Name, string Html)>> RenderAsync(Sql db, PeopleDirectory directory, string tenant,
        string templateBody, IEnumerable<Guid> employeeIds, bool canSeePay, CancellationToken ct)
    {
        var people = (await directory.ListAsync(tenant, ct, includeTerminated: true)).ToDictionary(p => p.Id);
        var company = (await db.QueryAsync("SELECT \"Name\", \"TaxNumber\" FROM platform_tenants WHERE \"Slug\" = $1",
            r => (Name: r.GetString(0), Tax: r.Str(1)), ct, tenant)).FirstOrDefault();
        var ids = employeeIds.Distinct().ToArray();
        var phones = (await db.QueryAsync("SELECT \"Id\", \"Phone\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = ANY($2)",
            r => (Id: r.GetGuid(0), Phone: r.Str(1)), ct, tenant, ids)).ToDictionary(x => x.Id, x => x.Phone);
        var leave = (await db.QueryAsync("""
            SELECT "EmployeeId", sum("EntitledDays" - "UsedDays") FROM leave_balances
            WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2) AND "Type" = 'Annual' AND "Year" = $3 GROUP BY 1
            """, r => (Id: r.GetGuid(0), Days: r.Dec(1) ?? 0), ct, tenant, ids, DateTime.UtcNow.Year)).ToDictionary(x => x.Id, x => x.Days);
        var pay = canSeePay
            ? (await db.QueryAsync("""
                SELECT DISTINCT ON ("EmployeeId") "EmployeeId", "BaseSalary", "Currency" FROM compensation_records
                WHERE "TenantSlug" = $1 AND "EmployeeId" = ANY($2) ORDER BY "EmployeeId", "EffectiveFrom" DESC
                """, r => (Id: r.GetGuid(0), Pay: r.GetDecimal(1), Cur: r.Str(2) ?? "TRY"), ct, tenant, ids)).ToDictionary(x => x.Id)
            : new();
        var tr = new System.Globalization.CultureInfo("tr-TR");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var docs = new List<(Guid EmployeeId, string Name, string Html)>();
        var seq = 0;
        foreach (var eid in ids)
        {
            if (!people.TryGetValue(eid, out var p)) continue;
            var head = p.DepartmentHeadId is { } h && people.TryGetValue(h, out var hp) ? hp.Name : "—";
            var parts = p.Name.Split(' ', 2);
            var values = new Dictionary<string, string>
            {
                ["calisan.adSoyad"] = p.Name, ["calisan.ad"] = parts[0], ["calisan.soyad"] = parts.Length > 1 ? parts[1] : "",
                ["calisan.eposta"] = p.Email ?? "—", ["calisan.telefon"] = phones.GetValueOrDefault(p.Id) ?? "—",
                ["calisan.pozisyon"] = p.Position ?? "—", ["calisan.departman"] = p.Department ?? "—",
                ["calisan.iseGiris"] = p.HireDate.ToString("dd.MM.yyyy"),
                ["calisan.kidemYil"] = Math.Round((today.DayNumber - p.HireDate.DayNumber) / 365.25, 1).ToString(tr),
                ["calisan.yoneticisi"] = head,
                ["izin.kalanYillik"] = leave.GetValueOrDefault(p.Id).ToString("0.#", tr),
                ["ucret.brut"] = pay.TryGetValue(p.Id, out var pr) ? pr.Pay.ToString("N2", tr) + " " + pr.Cur : "—",
                ["sirket.ad"] = company.Name ?? tenant, ["sirket.vergiNo"] = company.Tax ?? "—",
                ["bugun"] = today.ToString("dd.MM.yyyy"), ["belge.no"] = $"{DateTime.UtcNow:yyMMdd}-{++seq:000}",
            };
            var html = Regex.Replace(templateBody, @"\{\{\s*([\w.]+)\s*\}\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? WebUtility.HtmlEncode(v) : m.Value);
            docs.Add((p.Id, p.Name, html));
        }
        return docs;
    }
}
