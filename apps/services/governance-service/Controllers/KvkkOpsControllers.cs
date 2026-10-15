using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Models;

namespace GovernanceService.Controllers;

/// <summary>
/// Kiracının düzenlediği aydınlatma/açık rıza metinleri. Kiracı bir tipi yeniden
/// yayımlamadıysa yerleşik metin geçerlidir. Sürüm değişince eski onaylar "güncel değil"
/// görünür ve kullanıcıdan yeniden okuması istenir.
/// </summary>
public static class ConsentCatalog
{
    public static async Task<PrivacyController.ConsentType[]> EffectiveAsync(GovernanceDbContext db, CancellationToken ct)
    {
        var notices = await db.PrivacyNotices.AsNoTracking().OrderByDescending(n => n.PublishedAt).ToListAsync(ct);
        return PrivacyController.ConsentTypes.Select(t =>
        {
            var n = notices.FirstOrDefault(x => x.Type == t.Type);
            return n is null ? t : t with { Title = n.Title, Version = n.Version, Text = n.Text };
        }).ToArray();
    }
}

/* ======================================================================
 * KVKK operasyonları: aydınlatma metni sürümleri (K2), veri ihlali
 * yönetimi (K3), ilgili kişi başvurusunda kimlik doğrulama ve yanıt
 * şablonları (K8), gizlilik etki kontrol listesi (K9).
 * ==================================================================== */
[Route("api/privacy")]
[Authorize]
public class KvkkOpsController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly Notifier _notifier;
    public KvkkOpsController(GovernanceDbContext db, Notifier notifier) { _db = db; _notifier = notifier; }

    private async Task AuditAsync(string entity, string id, string action, object changes, CancellationToken ct) =>
        await Db.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","OccurredAt")
            VALUES ($1,'governance-service',$2,$3,$4,$5::jsonb,$6,$7,now())
            """, ct, Tenant, entity, id, action, JsonSerializer.Serialize(changes), Me.UserId, Me.Name);

    /* ------------------------------------------------------------------ K2 aydınlatma metinleri */

    [HttpGet("notices")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Notices(CancellationToken ct)
    {
        var all = await _db.PrivacyNotices.AsNoTracking().OrderByDescending(n => n.PublishedAt).ToListAsync(ct);
        var current = await ConsentCatalog.EffectiveAsync(_db, ct);
        var consents = await _db.Consents.AsNoTracking().ToListAsync(ct);
        var latest = consents.GroupBy(c => (c.UserId, c.ConsentType)).Select(g => g.OrderByDescending(c => c.RecordedAt).First()).ToList();
        return Ok(current.Select(t => new
        {
            t.Type, t.Title, t.Version, t.Required, t.Text,
            custom = all.Any(n => n.Type == t.Type),
            builtIn = PrivacyController.ConsentTypes.First(x => x.Type == t.Type).Text,
            // Aydınlatma "okundu" kaydıdır; rıza tipleri "verildi" — ikisi ayrı sayılır.
            acknowledgedCurrent = latest.Count(c => c.ConsentType == t.Type && c.Granted && c.Version == t.Version),
            onOlderVersion = latest.Count(c => c.ConsentType == t.Type && c.Version != t.Version),
            history = all.Where(n => n.Type == t.Type).Select(n => new { n.Id, n.Version, n.Title, n.ChangeNote, n.PublishedBy, n.PublishedAt }),
        }));
    }

    public record NoticeInput(string Type, string Title, string Text, string? Version, string? ChangeNote);

    /// <summary>Yeni sürüm yayımlar (eski sürümler silinmez — kim hangi metni okudu kanıtı).</summary>
    [HttpPost("notices")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> PublishNotice(NoticeInput body, CancellationToken ct)
    {
        var t = PrivacyController.ConsentTypes.FirstOrDefault(x => x.Type == body.Type);
        if (t is null) return BadRequest(new { message = L("Bilinmeyen metin tipi.", "Unknown notice type.") });
        var title = body.Title?.Trim() ?? "";
        var text = body.Text?.Trim() ?? "";
        if (title.Length is < 3 or > 200) return BadRequest(new { message = L("Başlık 3–200 karakter olmalı.", "Title must be 3–200 characters.") });
        if (text.Length is < 20 or > 20000) return BadRequest(new { message = L("Metin 20–20000 karakter olmalı.", "Text must be 20–20000 characters.") });
        var current = (await ConsentCatalog.EffectiveAsync(_db, ct)).First(x => x.Type == t.Type);
        var version = string.IsNullOrWhiteSpace(body.Version) ? $"{DateTime.UtcNow:yyyy.MM.dd-HHmm}" : body.Version.Trim();
        if (version.Length > 40) return BadRequest(new { message = L("Sürüm en fazla 40 karakter.", "Version must be at most 40 characters.") });
        if (version == current.Version) return Conflict(new { message = L("Bu sürüm zaten yayında; yeni bir sürüm adı verin.", "This version is already published; use a new version name.") });
        if (await _db.PrivacyNotices.AnyAsync(n => n.Type == t.Type && n.Version == version, ct))
            return Conflict(new { message = L("Bu sürüm adı daha önce kullanıldı.", "This version name was used before.") });
        var n = new PrivacyNotice
        {
            Type = t.Type, Version = version, Title = title, Text = text,
            ChangeNote = string.IsNullOrWhiteSpace(body.ChangeNote) ? null : body.ChangeNote.Trim(), PublishedBy = Me.Name,
        };
        _db.PrivacyNotices.Add(n);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("PrivacyNotice", n.Id.ToString(), "Published", new { n.Type, n.Version }, ct);
        // Dalga 10 (madde 55): yeni sürüm yeniden onay kampanyası başlatır (önceki açık kampanya kapanır).
        await ConsentCampaigns.StartAsync(Db, Tenant, n.Type, n.Version, current.Version, Me.Name, ct);
        return Ok(n);
    }

    /// <summary>Herkese açık değil ama tüm çalışanlar okuyabilir: belirli bir sürümün metni (geçmiş kanıtı).</summary>
    [HttpGet("notices/{type}/{version}")]
    public async Task<IActionResult> NoticeVersion(string type, string version, CancellationToken ct)
    {
        var n = await _db.PrivacyNotices.AsNoTracking().FirstOrDefaultAsync(x => x.Type == type && x.Version == version, ct);
        if (n is not null) return Ok(new { n.Type, n.Version, n.Title, n.Text, n.PublishedAt });
        var b = PrivacyController.ConsentTypes.FirstOrDefault(x => x.Type == type && x.Version == version);
        return b is null ? NotFound() : Ok(new { b.Type, b.Version, b.Title, b.Text, PublishedAt = (DateTime?)null });
    }

    /* ------------------------------------------------------------------ K3 veri ihlali */

    public const int BoardDeadlineHours = 72;

    private static object View(DataBreach b)
    {
        var deadline = b.DetectedAt.AddHours(BoardDeadlineHours);
        var affected = JsonSerializer.Deserialize<List<Guid>>(b.AffectedEmployeesJson) ?? new();
        return new
        {
            b.Id, b.Title, b.Description, b.DetectedAt, b.OccurredAt, b.DataCategories, b.AffectedCount, affectedEmployees = affected,
            b.Severity, b.Cause, b.Measures, b.Status, b.ReportedToBoardAt, b.BoardReference, b.SubjectsNotifiedAt, b.CreatedBy, b.CreatedAt,
            boardDeadline = deadline,
            hoursLeft = b.ReportedToBoardAt is null ? Math.Round((deadline - DateTime.UtcNow).TotalHours, 1) : (double?)null,
            overdue = b.ReportedToBoardAt is null && b.Status != "Closed" && deadline < DateTime.UtcNow,
            lateReport = b.ReportedToBoardAt is { } r && r > deadline,
        };
    }

    [HttpGet("breaches")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Breaches(CancellationToken ct) =>
        Ok((await _db.DataBreaches.AsNoTracking().OrderByDescending(b => b.DetectedAt).ToListAsync(ct)).Select(View));

    public record BreachInput(string Title, string Description, DateTime? DetectedAt, DateTime? OccurredAt, string? DataCategories,
        int? AffectedCount, List<Guid>? AffectedEmployees, string? Severity, string? Cause, string? Measures);

    private IActionResult? Validate(BreachInput body)
    {
        if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 200) return BadRequest(new { message = L("Başlık gerekli (en fazla 200 karakter).", "Title is required (max 200 characters).") });
        if (string.IsNullOrWhiteSpace(body.Description) || body.Description.Length > 10000) return BadRequest(new { message = L("Açıklama gerekli.", "Description is required.") });
        if (body.Severity is not (null or "Low" or "Medium" or "High")) return BadRequest(new { message = L("Geçersiz önem derecesi.", "Invalid severity.") });
        if (body.DetectedAt is { } d && d > DateTime.UtcNow.AddMinutes(5)) return BadRequest(new { message = L("Tespit zamanı gelecekte olamaz.", "Detection time cannot be in the future.") });
        if (body.OccurredAt is { } o && body.DetectedAt is { } d2 && o > d2) return BadRequest(new { message = L("İhlal zamanı tespitten sonra olamaz.", "The breach cannot occur after detection.") });
        if (body.AffectedCount is < 0) return BadRequest(new { message = L("Etkilenen sayısı negatif olamaz.", "Affected count cannot be negative.") });
        return null;
    }

    private static void Apply(DataBreach b, BreachInput body)
    {
        b.Title = body.Title.Trim();
        b.Description = body.Description.Trim();
        if (body.DetectedAt is { } d) b.DetectedAt = d.ToUniversalTime();
        b.OccurredAt = body.OccurredAt?.ToUniversalTime();
        b.DataCategories = string.IsNullOrWhiteSpace(body.DataCategories) ? null : body.DataCategories.Trim();
        var ids = (body.AffectedEmployees ?? new()).Distinct().Take(10000).ToList();
        b.AffectedEmployeesJson = JsonSerializer.Serialize(ids);
        b.AffectedCount = body.AffectedCount ?? (ids.Count > 0 ? ids.Count : null);
        b.Severity = body.Severity ?? "Medium";
        b.Cause = string.IsNullOrWhiteSpace(body.Cause) ? null : body.Cause.Trim();
        b.Measures = string.IsNullOrWhiteSpace(body.Measures) ? null : body.Measures.Trim();
    }

    [HttpPost("breaches")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateBreach(BreachInput body, CancellationToken ct)
    {
        if (Validate(body) is { } bad) return bad;
        var b = new DataBreach { CreatedBy = Me.Name };
        Apply(b, body);
        _db.DataBreaches.Add(b);
        // "Created" denetim satırını EF AuditInterceptor yazar (tüm alanlarla); burada ayrıca yazılırsa çift satır oluşur.
        await _db.SaveChangesAsync(ct);
        return Ok(View(b));
    }

    [HttpPut("breaches/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateBreach(Guid id, BreachInput body, CancellationToken ct)
    {
        var b = await _db.DataBreaches.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        if (b.Status == "Closed") return Conflict(new { message = L("Kapatılmış kayıt değiştirilemez.", "A closed record cannot be changed.") });
        if (Validate(body) is { } bad) return bad;
        Apply(b, body);
        // Alan değişiklikleri AuditInterceptor ile "Updated" olarak yazılır; ayrıca AuditAsync çift satır üretiyordu.
        await _db.SaveChangesAsync(ct);
        return Ok(View(b));
    }

    public record ReportInput(DateTime? ReportedAt, string? Reference);

    [HttpPost("breaches/{id:guid}/report")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> MarkReported(Guid id, ReportInput body, CancellationToken ct)
    {
        var b = await _db.DataBreaches.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        var at = (body.ReportedAt ?? DateTime.UtcNow).ToUniversalTime();
        if (at < b.DetectedAt.AddMinutes(-1) || at > DateTime.UtcNow.AddMinutes(5))
            return BadRequest(new { message = L("Bildirim zamanı tespitle şimdi arasında olmalı.", "Report time must be between detection and now.") });
        b.ReportedToBoardAt = at;
        b.BoardReference = string.IsNullOrWhiteSpace(body.Reference) ? null : body.Reference.Trim();
        if (b.Status == "Open") b.Status = "Reported";
        await _db.SaveChangesAsync(ct);
        await AuditAsync("DataBreach", b.Id.ToString(), "ReportedToBoard", new { b.BoardReference }, ct);
        return Ok(View(b));
    }

    public record NotifyInput(string? Message);

    /// <summary>
    /// Etkilenen çalışanlara uygulama içi bilgilendirme. Metin ihlalin niteliğini, olası
    /// sonuçlarını ve alınan önlemleri anlatır; başka kişilerin verisini içermez.
    /// </summary>
    [HttpPost("breaches/{id:guid}/notify")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> NotifySubjects(Guid id, NotifyInput body, CancellationToken ct)
    {
        var b = await _db.DataBreaches.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        var ids = JsonSerializer.Deserialize<List<Guid>>(b.AffectedEmployeesJson) ?? new();
        if (ids.Count == 0) return BadRequest(new { message = L("Etkilenen çalışan seçilmemiş.", "No affected employees selected.") });
        var msg = string.IsNullOrWhiteSpace(body.Message)
            ? $"Kişisel verilerinizi etkileyebilecek bir güvenlik olayı tespit edildi.\n\nNe oldu: {b.Title}\nEtkilenen veri türleri: {b.DataCategories ?? "inceleniyor"}\nAlınan önlemler: {b.Measures ?? "inceleniyor"}\n\nSorularınız için İK ile iletişime geçebilir ya da KVKK başvurusu yapabilirsiniz."
            : body.Message.Trim();
        // G2: hazır metin alıcının diline göre; İK'nın yazdığı özel metin olduğu gibi gider.
        var msgEn = string.IsNullOrWhiteSpace(body.Message)
            ? $"A security incident that may affect your personal data has been detected.\n\nWhat happened: {b.Title}\nData categories affected: {b.DataCategories ?? "under investigation"}\nMeasures taken: {b.Measures ?? "under investigation"}\n\nFor questions, contact HR or submit a KVKK request."
            : msg;
        if (msg.Length > 4000) return BadRequest(new { message = L("Mesaj en fazla 4000 karakter.", "Message must be at most 4000 characters.") });
        foreach (var e in ids)
            await _notifier.LocalizedAsync(Tenant, e, "Kişisel veri güvenliği bilgilendirmesi", "Personal data security notice", msg, msgEn, "privacy.breach", ct);
        b.SubjectsNotifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("DataBreach", b.Id.ToString(), "SubjectsNotified", new { count = ids.Count }, ct);
        return Ok(new { notified = ids.Count });
    }

    [HttpPost("breaches/{id:guid}/close")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CloseBreach(Guid id, CancellationToken ct)
    {
        var b = await _db.DataBreaches.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        if (string.IsNullOrWhiteSpace(b.Measures)) return BadRequest(new { message = L("Kapatmadan önce alınan önlemleri yazın.", "Describe the measures taken before closing.") });
        b.Status = "Closed";
        await _db.SaveChangesAsync(ct);
        await AuditAsync("DataBreach", b.Id.ToString(), "Closed", new { }, ct);
        return Ok(View(b));
    }

    /// <summary>Kurul'un "Veri İhlali Bildirim Formu" alanlarına göre doldurulmuş taslak (metin).</summary>
    [HttpGet("breaches/{id:guid}/board-form")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> BoardForm(Guid id, CancellationToken ct)
    {
        var b = await _db.DataBreaches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        var org = await Db.QueryAsync("SELECT \"Name\" FROM platform_tenants WHERE \"Slug\" = $1", r => r.GetString(0), ct, Tenant);
        string F(DateTime? d) => d is null ? "—" : d.Value.AddHours(3).ToString("dd.MM.yyyy HH:mm");
        var late = DateTime.UtcNow > b.DetectedAt.AddHours(BoardDeadlineHours) && b.ReportedToBoardAt is null;
        var sb = new StringBuilder();
        sb.AppendLine("KİŞİSEL VERİ İHLALİ BİLDİRİM FORMU — TASLAK");
        sb.AppendLine("(Kişisel Verileri Koruma Kurulu'na verbis.kvkk.gov.tr üzerinden iletilir. Bu taslak HR360 kaydından üretilmiştir; göndermeden önce kontrol edin.)");
        sb.AppendLine();
        sb.AppendLine($"1. Veri sorumlusu: {org.FirstOrDefault() ?? Tenant}");
        sb.AppendLine($"2. İhlalin başlığı: {b.Title}");
        sb.AppendLine($"3. İhlalin gerçekleştiği tarih/saat: {F(b.OccurredAt)}");
        sb.AppendLine($"4. İhlalin tespit edildiği tarih/saat: {F(b.DetectedAt)}");
        sb.AppendLine($"5. İhlalin kaynağı/nedeni: {b.Cause ?? "—"}");
        sb.AppendLine($"6. İhlalin açıklaması: {b.Description}");
        sb.AppendLine($"7. Etkilenen kişisel veri kategorileri: {b.DataCategories ?? "—"}");
        sb.AppendLine($"8. Etkilenen kişi sayısı: {(b.AffectedCount?.ToString() ?? "—")} (ilgili kişi grubu: çalışanlar)");
        sb.AppendLine($"9. Önem derecesi (iç değerlendirme): {b.Severity switch { "High" => "Yüksek", "Low" => "Düşük", _ => "Orta" }}");
        sb.AppendLine($"10. Alınan/alınacak tedbirler: {b.Measures ?? "—"}");
        sb.AppendLine($"11. İlgili kişilere bildirim: {(b.SubjectsNotifiedAt is null ? "Henüz yapılmadı" : F(b.SubjectsNotifiedAt) + " tarihinde uygulama içi bildirimle")}");
        if (late) sb.AppendLine("12. Geç bildirim gerekçesi: (72 saat aşıldı — gecikmenin nedenini yazın)");
        return Ok(new { text = sb.ToString(), late });
    }

    /* ------------------------------------------------------------------ K8 başvuru kimlik doğrulama + şablonlar */

    public record ExternalRequestInput(string Kind, string PersonName, Guid? EmployeeId, string Channel, string? Contact, string? Details, DateTime? ReceivedAt);

    /// <summary>
    /// E-posta, KEP, posta ya da elden gelen başvurunun İK tarafından kaydı. 30 günlük süre
    /// başvurunun veri sorumlusuna ulaştığı tarihten başlar. Kimlik doğrulanana kadar
    /// başvurucuya kişisel veri içeren yanıt verilemez.
    /// </summary>
    [HttpPost("requests/external")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateExternal(ExternalRequestInput body, CancellationToken ct)
    {
        if (body.Kind is not ("Access" or "Rectification" or "Erasure" or "Objection"))
            return BadRequest(new { message = L("Geçersiz başvuru türü.", "Invalid request type.") });
        if (body.Channel is not ("Email" or "Kep" or "Mail" or "InPerson"))
            return BadRequest(new { message = L("Geçersiz kanal.", "Invalid channel.") });
        if (string.IsNullOrWhiteSpace(body.PersonName) || body.PersonName.Length > 200)
            return BadRequest(new { message = L("Başvurucunun adı gerekli.", "Applicant name is required.") });
        var received = (body.ReceivedAt ?? DateTime.UtcNow).ToUniversalTime();
        if (received > DateTime.UtcNow.AddMinutes(5) || received < DateTime.UtcNow.AddDays(-30))
            return BadRequest(new { message = L("Başvuru tarihi son 30 gün içinde olmalı.", "The receipt date must be within the last 30 days.") });
        string? userId = null;
        if (body.EmployeeId is { } eid)
        {
            var p = await People.FindAsync(Tenant, eid, ct);
            if (p is null) return BadRequest(new { message = L("Çalışan bulunamadı.", "Employee not found.") });
            userId = p.UserId;
        }
        var r = new DataRequest
        {
            UserId = userId ?? "", EmployeeId = body.EmployeeId, PersonName = body.PersonName.Trim(), Kind = body.Kind,
            Details = body.Details?.Trim(), Channel = body.Channel, Contact = body.Contact?.Trim(),
            CreatedAt = received, DueAt = received.AddDays(30), IdentityVerified = false,
        };
        _db.DataRequests.Add(r);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("DataRequest", r.Id.ToString(), "Received", new { r.Channel, r.Kind }, ct);
        return Ok(r);
    }

    public static readonly Dictionary<string, string> VerificationMethods = new()
    {
        ["Session"] = "HR360 oturumu (kendi hesabından başvuru)",
        ["Kep"] = "KEP adresi (kayıtlı elektronik posta)",
        ["ESignature"] = "Güvenli elektronik imza",
        ["RegisteredEmail"] = "Sistemde kayıtlı e-posta adresinden gelen başvuru",
        ["InPersonId"] = "Elden başvuruda kimlik belgesi görüldü",
        ["NotarizedMail"] = "Noter onaylı / ıslak imzalı posta",
    };

    public record VerifyInput(string Method, string? Note);

    [HttpPost("requests/{id:guid}/verify")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Verify(Guid id, VerifyInput body, CancellationToken ct)
    {
        var r = await _db.DataRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (!VerificationMethods.ContainsKey(body.Method)) return BadRequest(new { message = L("Geçersiz doğrulama yöntemi.", "Invalid verification method.") });
        r.IdentityVerified = true;
        r.VerificationMethod = body.Method;
        r.VerifiedBy = Me.Name;
        r.VerifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        // Kimlik belgesinin kendisi saklanmaz; yalnızca yöntem ve doğrulayan kaydedilir.
        await AuditAsync("DataRequest", r.Id.ToString(), "IdentityVerified", new { method = body.Method }, ct);
        // Dalga 10: kimliği doğrulanan erişim başvurusunda veri paketi otomatik hazırlanır.
        if (r.Kind == "Access" && r.EmployeeId is not null && r.Status is "Received" or "InProgress")
        {
            try { await DataRequestPackages.CreateAsync(Db, Tenant, r.Id, People, Me.UserId, Me.Name + " (kimlik doğrulama sonrası otomatik)", ct); }
            catch (Exception ex) when (ex is Npgsql.PostgresException or InvalidOperationException) { }
        }
        return Ok(new { r.Id, r.IdentityVerified, r.VerificationMethod, r.VerifiedBy, r.VerifiedAt });
    }

    [HttpGet("request-meta")]
    [Authorize(Policy = "RequireHrAdmin")]
    public IActionResult RequestMeta() => Ok(new
    {
        verificationMethods = VerificationMethods.Select(kv => new { value = kv.Key, label = kv.Value }),
        channels = new[] { "Email", "Kep", "Mail", "InPerson" },
        templates = ResponseTemplates.Select(t => new { t.Key, t.Kind, t.Outcome, t.Title, t.Text }),
    });

    public sealed record ResponseTemplate(string Key, string Kind, string Outcome, string Title, string Text);

    /// <summary>KVKK m.13 yanıt şablonları ({ad} ve {tarih} yer tutucuları arayüzde doldurulur).</summary>
    public static readonly ResponseTemplate[] ResponseTemplates =
    {
        new("access-ok", "Access", "Completed", "Bilgi talebi — karşılandı",
            "Sayın {ad},\n\n{tarih} tarihli başvurunuz KVKK m.11 kapsamında incelenmiştir. Hakkınızda işlenen kişisel verilerin dökümü ekte/HR360 Gizlilik ekranında sunulmuştur. Verileriniz iş sözleşmesinin ifası, yasal yükümlülükler ve meşru menfaat kapsamında işlenmekte; yurt içinde yalnızca yasal merciler ve hizmet sağlayıcılarla paylaşılmaktadır.\n\nSaygılarımızla"),
        new("rect-ok", "Rectification", "Completed", "Düzeltme talebi — düzeltildi",
            "Sayın {ad},\n\n{tarih} tarihli başvurunuzda belirttiğiniz eksik/yanlış kişisel verileriniz düzeltilmiştir. Verilerin aktarıldığı üçüncü kişilere de düzeltme bildirilmiştir (KVKK m.11/1-f).\n\nSaygılarımızla"),
        new("erase-ok", "Erasure", "Completed", "Silme talebi — silindi/anonimleştirildi",
            "Sayın {ad},\n\n{tarih} tarihli başvurunuz üzerine, işlenme şartları ortadan kalkan kişisel verileriniz silinmiş/anonim hâle getirilmiştir. Yasal saklama yükümlülüğü bulunan kayıtlar (ör. SGK, vergi) süre sonunda imha edilecektir.\n\nSaygılarımızla"),
        new("erase-legal", "Erasure", "Rejected", "Silme talebi — yasal saklama nedeniyle ret",
            "Sayın {ad},\n\n{tarih} tarihli silme talebiniz incelenmiştir. Talep konusu verilerin işlenmesi kanunlarda açıkça öngörüldüğünden ve hukuki yükümlülüğümüzün yerine getirilmesi için zorunlu olduğundan (KVKK m.5/2-a, ç) saklama süresi dolana kadar silinememektedir. Süre sonunda verileriniz imha edilecektir. Bu yanıta karşı 30 gün içinde Kişisel Verileri Koruma Kurulu'na şikâyette bulunabilirsiniz.\n\nSaygılarımızla"),
        new("objection-ok", "Objection", "Completed", "İtiraz — kabul",
            "Sayın {ad},\n\n{tarih} tarihli itirazınız kabul edilmiş, itiraz konusu işleme durdurulmuştur.\n\nSaygılarımızla"),
        new("identity", "*", "InProgress", "Kimlik doğrulama isteği",
            "Sayın {ad},\n\nBaşvurunuzu yanıtlayabilmemiz için kimliğinizi doğrulamamız gerekiyor. Lütfen başvurunuzu sistemde kayıtlı e-posta adresinizden, KEP adresinizden ya da güvenli elektronik imzalı olarak yineleyin veya kimlik belgenizle İK'ya başvurun. Kimlik belgenizin kopyası saklanmaz.\n\nSaygılarımızla"),
    };

    /* ------------------------------------------------------------------ K9 gizlilik etki kontrol listesi */

    public sealed record Question(string Code, string Text, string RiskyAnswer, int Weight);

    public static readonly Question[] Questions =
    {
        new("special", "Özel nitelikli kişisel veri (sağlık, biyometrik, sendika, din vb.) işleniyor mu?", "yes", 3),
        new("abroad", "Veri yurt dışında bir sunucuya/sağlayıcıya aktarılıyor mu?", "yes", 3),
        new("automated", "Kişi hakkında otomatik karar ya da profil çıkarımı yapılıyor mu?", "yes", 2),
        new("large", "Tüm çalışanları ya da 250'den fazla kişiyi kapsıyor mu?", "yes", 1),
        new("monitoring", "Çalışanların sistematik izlenmesi (konum, ekran, iletişim) söz konusu mu?", "yes", 3),
        new("minimal", "Yalnızca amaç için gerekli alanlar mı toplanıyor? (veri en aza indirme)", "no", 2),
        new("basis", "Her veri için hukuki sebep (m.5/m.6) belirlendi mi?", "no", 2),
        new("notice", "Aydınlatma metni bu işlemeyi kapsayacak şekilde güncellendi mi?", "no", 1),
        new("retention", "Saklama süresi ve imha yöntemi belirlendi mi?", "no", 1),
        new("access", "Erişim yalnızca gerekli rollere mi sınırlı ve erişim kaydı tutuluyor mu?", "no", 2),
        new("encryption", "Aktarımda ve depolamada şifreleme var mı?", "no", 2),
        new("contract", "Sağlayıcı ile veri işleme sözleşmesi (veri işleyen) imzalandı mı?", "no", 1),
    };

    public static string ComputeRisk(Dictionary<string, AnswerInput> answers)
    {
        var score = 0;
        foreach (var q in Questions)
            if (answers.TryGetValue(q.Code, out var a) && a.Answer == q.RiskyAnswer) score += q.Weight;
        var specialAbroad = answers.TryGetValue("special", out var s) && s.Answer == "yes" && answers.TryGetValue("abroad", out var ab) && ab.Answer == "yes";
        return specialAbroad || score >= 8 ? "High" : score >= 4 ? "Medium" : "Low";
    }

    public record AnswerInput(string Answer, string? Note);
    public record AssessmentInput(string Subject, string Kind, string? ProviderKey, Dictionary<string, AnswerInput> Answers);

    [HttpGet("assessments/questions")]
    [Authorize(Policy = "RequireHrAdmin")]
    public IActionResult AssessmentQuestions() => Ok(new
    {
        questions = Questions.Select(q => new { q.Code, q.Text, q.RiskyAnswer, q.Weight }),
        providers = PrivacyCatalog.Providers.Select(p => new { p.Key, p.Name }),
    });

    private static object View(PrivacyAssessment a) => new
    {
        a.Id, a.Subject, a.Kind, a.ProviderKey, answers = JsonSerializer.Deserialize<Dictionary<string, AnswerInput>>(a.AnswersJson),
        a.Risk, a.Status, a.CreatedBy, a.ApprovedBy, a.ApprovedAt, a.UpdatedAt,
    };

    [HttpGet("assessments")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Assessments(CancellationToken ct) =>
        Ok((await _db.PrivacyAssessments.AsNoTracking().OrderByDescending(a => a.UpdatedAt).ToListAsync(ct)).Select(View));

    private IActionResult? ValidateAssessment(AssessmentInput body)
    {
        if (string.IsNullOrWhiteSpace(body.Subject) || body.Subject.Length > 200) return BadRequest(new { message = L("Konu gerekli.", "Subject is required.") });
        if (body.Kind is not ("Integration" or "CustomField" or "Process")) return BadRequest(new { message = L("Geçersiz tür.", "Invalid kind.") });
        if (body.ProviderKey is not null && PrivacyCatalog.Providers.All(p => p.Key != body.ProviderKey)) return BadRequest(new { message = L("Bilinmeyen sağlayıcı.", "Unknown provider.") });
        foreach (var (k, v) in body.Answers ?? new())
        {
            if (Questions.All(q => q.Code != k)) return BadRequest(new { message = L($"Bilinmeyen soru: {k}", $"Unknown question: {k}") });
            if (v.Answer is not ("yes" or "no" or "na")) return BadRequest(new { message = L("Yanıt evet/hayır/uygulanmaz olmalı.", "Answer must be yes/no/na.") });
            if (v.Note is { Length: > 1000 }) return BadRequest(new { message = L("Not en fazla 1000 karakter.", "Note must be at most 1000 characters.") });
        }
        return null;
    }

    [HttpPost("assessments")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> CreateAssessment(AssessmentInput body, CancellationToken ct)
    {
        if (ValidateAssessment(body) is { } bad) return bad;
        var a = new PrivacyAssessment
        {
            Subject = body.Subject.Trim(), Kind = body.Kind, ProviderKey = body.ProviderKey,
            AnswersJson = JsonSerializer.Serialize(body.Answers ?? new()), Risk = ComputeRisk(body.Answers ?? new()), CreatedBy = Me.Name,
        };
        _db.PrivacyAssessments.Add(a);
        await _db.SaveChangesAsync(ct);
        return Ok(View(a));
    }

    [HttpPut("assessments/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateAssessment(Guid id, AssessmentInput body, CancellationToken ct)
    {
        var a = await _db.PrivacyAssessments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (ValidateAssessment(body) is { } bad) return bad;
        a.Subject = body.Subject.Trim(); a.Kind = body.Kind; a.ProviderKey = body.ProviderKey;
        a.AnswersJson = JsonSerializer.Serialize(body.Answers ?? new());
        a.Risk = ComputeRisk(body.Answers ?? new());
        // Yanıt değişince onay düşer: yeniden gözden geçirilmeli.
        a.Status = "Draft"; a.ApprovedBy = null; a.ApprovedAt = null; a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(View(a));
    }

    [HttpPost("assessments/{id:guid}/approve")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ApproveAssessment(Guid id, CancellationToken ct)
    {
        var a = await _db.PrivacyAssessments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        var answers = JsonSerializer.Deserialize<Dictionary<string, AnswerInput>>(a.AnswersJson) ?? new();
        var missing = Questions.Where(q => !answers.ContainsKey(q.Code)).Select(q => q.Code).ToList();
        if (missing.Count > 0) return BadRequest(new { message = L("Tüm sorular yanıtlanmalı.", "All questions must be answered."), missing });
        if (a.CreatedBy == Me.Name && a.Risk == "High")
            return StatusCode(403, new { message = L("Yüksek riskli değerlendirmeyi hazırlayan kişi onaylayamaz (dört göz ilkesi).", "A high-risk assessment cannot be approved by its author (four-eyes principle).") });
        a.Status = "Approved"; a.ApprovedBy = Me.Name; a.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("PrivacyAssessment", a.Id.ToString(), "Approved", new { a.Risk }, ct);
        return Ok(View(a));
    }

    [HttpDelete("assessments/{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> DeleteAssessment(Guid id, CancellationToken ct)
    {
        var a = await _db.PrivacyAssessments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (a.Status == "Approved") return Conflict(new { message = L("Onaylı değerlendirme silinemez (kanıt).", "An approved assessment cannot be deleted (evidence).") });
        _db.PrivacyAssessments.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /* ------------------------------------------------------------------ G20 alan düzeyinde yetki */

    public static readonly (string Field, string Label, bool Sensitive, string Default)[] Fields =
    {
        ("bio", "Hakkımda", false, "everyone"), ("pronouns", "Hitap", false, "everyone"), ("skills", "Beceriler", false, "everyone"),
        ("interests", "İlgi alanları", false, "everyone"), ("linkedInUrl", "LinkedIn", false, "everyone"),
        ("birthDate", "Doğum tarihi", false, "hr"), ("address", "Adres", false, "hr"), ("emergencyContact", "Acil durum kişisi", false, "hr"),
        ("iban", "IBAN", true, "hr"), ("nationalId", "T.C. kimlik no", true, "hr"),
    };
    private static readonly string[] Levels = { "everyone", "manager", "hr", "self" };

    [HttpGet("field-policies")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> FieldPolicies(CancellationToken ct)
    {
        var rows = await Db.QueryAsync("""SELECT "Field", "MinLevel", "UpdatedBy", "UpdatedAt" FROM governance_field_policies WHERE "TenantSlug" = $1""",
            r => (Field: r.GetString(0), Level: r.GetString(1), By: r.GetString(2), At: r.GetFieldValue<DateTime>(3)), ct, Tenant);
        return Ok(Fields.Select(f =>
        {
            var row = rows.FirstOrDefault(x => x.Field == f.Field);
            return new
            {
                field = f.Field, label = f.Label, sensitive = f.Sensitive, defaultLevel = f.Default,
                level = row.Field is null ? f.Default : row.Level, updatedBy = row.By, updatedAt = row.Field is null ? (DateTime?)null : row.At,
                allowed = f.Sensitive ? new[] { "hr", "self" } : Levels,
            };
        }));
    }

    public record FieldPolicyInput(string Level);

    [HttpPut("field-policies/{field}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> SetFieldPolicy(string field, FieldPolicyInput body, CancellationToken ct)
    {
        var f = Fields.FirstOrDefault(x => x.Field == field);
        if (f.Field is null) return NotFound();
        // TCKN/IBAN hiçbir zaman tüm çalışanlara ya da yöneticiye açılamaz (veri en aza indirme).
        if (!Levels.Contains(body.Level) || (f.Sensitive && body.Level is not ("hr" or "self")))
            return BadRequest(new { message = L("Bu alan için geçersiz düzey.", "Invalid level for this field.") });
        await Db.ExecuteAsync("""
            INSERT INTO governance_field_policies ("Id","TenantSlug","Field","MinLevel","SelfVisible","UpdatedBy","UpdatedAt")
            VALUES ($1,$2,$3,$4,true,$5,now())
            ON CONFLICT ("TenantSlug","Field") DO UPDATE SET "MinLevel" = EXCLUDED."MinLevel", "UpdatedBy" = EXCLUDED."UpdatedBy", "UpdatedAt" = now()
            """, ct, Guid.NewGuid(), Tenant, field, body.Level, Me.Name);
        await AuditAsync("FieldPolicy", field, "Updated", new { level = body.Level }, ct);
        return Ok(new { field, level = body.Level });
    }
}
