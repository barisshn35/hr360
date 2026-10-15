using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y15 Etik / ihbar hattı.
 *
 * ANONİMLİK İLKELERİ
 *  - Herkese açık uçlar oturum istemez; IP, kullanıcı kimliği, tarayıcı bilgisi
 *    OKUNMAZ ve SAKLANMAZ. Gateway bu yol için erişim kaydını kapatır ve IP
 *    başlıklarını iletmez (deploy/nginx/nginx.conf).
 *  - EF kullanılmaz: denetim önleyicisi (AuditInterceptor) IP/korelasyon yazardı.
 *  - Alınma tarihi gün hassasiyetinde; takip kodu 80 bit rastgele, yalnızca
 *    SHA-256 özeti saklanır; kod bir kez gösterilir.
 *  - Bildirimleri yalnızca kiracının atadığı etik kurulu görür; İK otomatik üye DEĞİLDİR.
 * ==================================================================== */

/// <summary>Herkese açık etik hattı (oturum gerekmez).</summary>
[ApiController]
[Route("api/ethics/public/{tenant}")]
[AllowAnonymous]
public class EthicsPublicController : ControllerBase
{
    // Kiracı başına saatte 20 yeni bildirim; kod başına 10 dakikada 30 sorgu/mesaj;
    // kiracı başına dakikada 30 geçersiz kod denemesi (kod tahmini zaten olanaksız: 80 bit).
    private static readonly WindowLimiter Intake = new(20, TimeSpan.FromHours(1));
    private static readonly WindowLimiter PerCode = new(30, TimeSpan.FromMinutes(10));
    private static readonly WindowLimiter BadCodes = new(30, TimeSpan.FromMinutes(1));

    private readonly Sql _sql;
    public EthicsPublicController(Sql sql) => _sql = sql;

    private async Task<(bool Ok, string? Company, int Members)> TenantAsync(string tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant) || tenant.Length > 64) return (false, null, 0);
        var t = (await _sql.QueryAsync("""
            SELECT t."Name", (SELECT count(*) FROM governance_ethics_committee c WHERE c."TenantSlug" = t."Slug")
            FROM platform_tenants t WHERE t."Slug" = $1 AND t."Status" = 'Active'
            """, r => (Name: r.GetString(0), Members: Convert.ToInt32(r.GetValue(1))), ct, tenant)).FirstOrDefault();
        return t.Name is null ? (false, null, 0) : (true, t.Name, t.Members);
    }

    [HttpGet]
    public async Task<IActionResult> Info(string tenant, CancellationToken ct)
    {
        var t = await TenantAsync(tenant, ct);
        if (!t.Ok) return NotFound(new { message = "Şirket bulunamadı." });
        return Ok(new
        {
            company = t.Company, enabled = t.Members > 0,
            categories = EthicsCode.Categories.Select(c => new { value = c.Key, label = c.Value }),
        });
    }

    /// <summary>
    /// Bot koruması (güvenlik dalgası 2B): form açılırken alınan imzalı zaman jetonu. Gönderim en erken
    /// 3 sn, en geç 2 saat sonra ve bir kez kabul edilir. IP ya da tarayıcı bilgisi kullanılmaz.
    /// </summary>
    [HttpGet("form-token")]
    public async Task<IActionResult> FormToken(string tenant, CancellationToken ct)
    {
        var t = await TenantAsync(tenant, ct);
        if (!t.Ok) return NotFound(new { message = "Şirket bulunamadı." });
        return Ok(new { token = FormGuard.Shared.Issue("ethics:" + tenant), minSeconds = (int)FormGuard.MinAge.TotalSeconds });
    }

    /// <param name="Website">Görünmez bot tuzağı alanı: insanlar boş bırakır.</param>
    public record ReportInput(string Category, string Description, string? Contact, string? FormToken = null, string? Website = null);

    [HttpPost("reports")]
    public async Task<IActionResult> Create(string tenant, [FromBody] ReportInput body, CancellationToken ct)
    {
        var t = await TenantAsync(tenant, ct);
        if (!t.Ok) return NotFound(new { message = "Şirket bulunamadı." });
        if (t.Members == 0) return Conflict(new { message = "Bu şirketin etik hattı henüz yapılandırılmamış." });
        if (!EthicsCode.Categories.ContainsKey(body.Category ?? "")) return BadRequest(new { message = "Geçersiz kategori." });
        var description = body.Description?.Trim() ?? "";
        if (description.Length is < 20 or > 10_000) return BadRequest(new { message = "Açıklama 20-10000 karakter olmalı." });
        var contact = string.IsNullOrWhiteSpace(body.Contact) ? null : body.Contact.Trim();
        if (contact is { Length: > 300 }) return BadRequest(new { message = "İletişim bilgisi en fazla 300 karakter olabilir." });
        var guard = FormGuard.Shared.Verify(body.FormToken, "ethics:" + tenant);
        if (guard != FormTokenStatus.Ok)
            return BadRequest(new { message = FormGuard.Message(guard), code = guard == FormTokenStatus.TooFast ? "form_too_fast" : "form_token" });
        // Bot tuzağı doldurulduysa kayıt yapılmadan başarı görünümü döner (bot ayırt edemesin).
        if (!string.IsNullOrEmpty(body.Website))
            return Ok(new { followUpCode = EthicsCode.New(), message = "Bildiriminiz alındı. Takip kodunuzu güvenli bir yere kaydedin; yalnızca bir kez gösterilir ve kaybolursa yeniden üretilemez." });
        if (!Intake.Allow(tenant)) return StatusCode(429, new { message = "Çok fazla bildirim alındı; lütfen daha sonra tekrar deneyin." });

        var code = EthicsCode.New();
        var id = Guid.NewGuid();
        await _sql.ExecuteAsync("""
            INSERT INTO governance_ethics_reports ("Id","TenantSlug","Category","Description","ContactEnc","CodeHash","Status","ReceivedOn","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,'Received',current_date,date_trunc('day', now()))
            """, ct, id, tenant, body.Category, description, contact is null ? null : SecretBox.Protect(contact), EthicsCode.Hash(code));
        await NotifyCommitteeAsync(tenant, "Yeni etik bildirimi alındı", "New ethics report received", ct);
        return Ok(new
        {
            followUpCode = code,
            message = "Bildiriminiz alındı. Takip kodunuzu güvenli bir yere kaydedin; yalnızca bir kez gösterilir ve kaybolursa yeniden üretilemez.",
        });
    }

    private async Task NotifyCommitteeAsync(string tenant, string subject, string subjectEn, CancellationToken ct)
    {
        var members = await _sql.QueryAsync("SELECT \"EmployeeId\" FROM governance_ethics_committee WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" IS NOT NULL",
            r => r.GetGuid(0), ct, tenant);
        await BulkNotifyLocalized.InAppAsync(_sql, tenant, members, subject, subjectEn, "Etik hattı gelen kutusunu açın.", "Open the ethics hotline inbox.", "ethics.report", ct);
    }

    private async Task<(Guid Id, string Status)?> FindAsync(string tenant, string? code, CancellationToken ct)
    {
        if (!EthicsCode.LooksValid(code)) return null;
        var r = (await _sql.QueryAsync("SELECT \"Id\", \"Status\" FROM governance_ethics_reports WHERE \"TenantSlug\" = $1 AND \"CodeHash\" = $2",
            x => (Id: x.GetGuid(0), Status: x.GetString(1)), ct, tenant, EthicsCode.Hash(code!))).FirstOrDefault();
        return r.Id == Guid.Empty ? null : r;
    }

    /// <summary>Durum ve yazışmalar. Kod URL'de değil başlıkta taşınır (X-Follow-Up-Code), kayıtlara düşmesin.</summary>
    [HttpGet("reports/status")]
    public async Task<IActionResult> Status(string tenant, CancellationToken ct)
    {
        var code = Request.Headers["X-Follow-Up-Code"].FirstOrDefault();
        return await StatusFor(tenant, code, ct);
    }

    public record CodeInput(string Code);

    /// <summary>Aynı sorgu, kod gövdede (başlık gönderemeyen istemciler için).</summary>
    [HttpPost("reports/status")]
    public Task<IActionResult> StatusPost(string tenant, [FromBody] CodeInput body, CancellationToken ct) => StatusFor(tenant, body.Code, ct);

    private async Task<IActionResult> StatusFor(string tenant, string? code, CancellationToken ct)
    {
        if (!PerCode.Allow(EthicsCode.Hash(code ?? ""))) return StatusCode(429, new { message = "Çok fazla deneme; birkaç dakika sonra tekrar deneyin." });
        var r = await FindAsync(tenant, code, ct);
        if (r is null)
        {
            if (!BadCodes.Allow(tenant)) return StatusCode(429, new { message = "Çok fazla deneme; birkaç dakika sonra tekrar deneyin." });
            return NotFound(new { message = "Bu takip koduyla bir bildirim bulunamadı." });
        }
        var head = (await _sql.QueryAsync("""
            SELECT "Category","Status","Outcome","ReceivedOn" FROM governance_ethics_reports WHERE "Id" = $1
            """, x => new { category = x.GetString(0), status = x.GetString(1), outcome = x.Str(2), receivedOn = x.GetFieldValue<DateOnly>(3) }, ct, r.Value.Id)).First();
        var messages = await _sql.QueryAsync("""
            SELECT "FromReporter","Author","Body","CreatedOn" FROM governance_ethics_messages WHERE "ReportId" = $1 ORDER BY "Seq"
            """, x => new { fromReporter = x.GetBoolean(0), author = x.Str(1), body = x.GetString(2), createdOn = x.GetFieldValue<DateOnly>(3) }, ct, r.Value.Id);
        return Ok(new
        {
            head.category, categoryLabel = EthicsCode.Categories.GetValueOrDefault(head.category, head.category),
            head.status, head.outcome, head.receivedOn, messages,
        });
    }

    public record MessageInput(string Code, string Body);

    [HttpPost("reports/messages")]
    public async Task<IActionResult> Message(string tenant, [FromBody] MessageInput body, CancellationToken ct)
    {
        if (!PerCode.Allow(EthicsCode.Hash(body.Code ?? ""))) return StatusCode(429, new { message = "Çok fazla deneme; birkaç dakika sonra tekrar deneyin." });
        var r = await FindAsync(tenant, body.Code, ct);
        if (r is null)
        {
            if (!BadCodes.Allow(tenant)) return StatusCode(429, new { message = "Çok fazla deneme; birkaç dakika sonra tekrar deneyin." });
            return NotFound(new { message = "Bu takip koduyla bir bildirim bulunamadı." });
        }
        if (r.Value.Status == "Closed") return Conflict(new { message = "Bu bildirim kapatılmış; yeni bir bildirim oluşturabilirsiniz." });
        var text = body.Body?.Trim() ?? "";
        if (text.Length is < 1 or > 5000) return BadRequest(new { message = "Mesaj 1-5000 karakter olmalı." });
        await _sql.ExecuteAsync("""
            INSERT INTO governance_ethics_messages ("Id","TenantSlug","ReportId","FromReporter","Author","Body","CreatedOn")
            VALUES ($1,$2,$3,true,NULL,$4,current_date)
            """, ct, Guid.NewGuid(), tenant, r.Value.Id, text);
        await _sql.ExecuteAsync("UPDATE governance_ethics_reports SET \"UpdatedAt\" = date_trunc('day', now()) WHERE \"Id\" = $1", ct, r.Value.Id);
        await NotifyCommitteeAsync(tenant, "Etik bildirimine yeni mesaj", "New message on an ethics report", ct);
        return Ok(new { sent = true });
    }
}

/// <summary>Etik kurulu gelen kutusu ve kurul yönetimi (şirket yöneticisi).</summary>
[Route("api/ethics")]
[Authorize]
public class EthicsController : AppController
{
    private bool CanManage => Me.Roles.Contains("tenant-admin");

    private async Task<bool> IsMemberAsync(CancellationToken ct) =>
        await Db.ScalarAsync("SELECT 1 FROM governance_ethics_committee WHERE \"TenantSlug\" = $1 AND \"UserId\" = $2", ct, Tenant, Me.UserId) is not null;

    private IActionResult NotMember() => StatusCode(403, new { message = L("Etik bildirimlerini yalnızca etik kurulu üyeleri görebilir.", "Only ethics committee members can view reports.") });

    [HttpGet("me")]
    public async Task<IActionResult> MeInfo(CancellationToken ct) => Ok(new { isMember = await IsMemberAsync(ct), canManage = CanManage, tenant = Tenant });

    /* -------------------------------------------------------------- kurul */

    [HttpGet("committee")]
    public async Task<IActionResult> Committee(CancellationToken ct)
    {
        if (!CanManage && !await IsMemberAsync(ct)) return StatusCode(403, new { message = L("Yetkiniz yok.", "Forbidden.") });
        return Ok(await Db.QueryAsync("""
            SELECT "Id","UserId","EmployeeId","Name","AddedBy","AddedAt" FROM governance_ethics_committee WHERE "TenantSlug" = $1 ORDER BY "Name"
            """, r => new { id = r.GetGuid(0), userId = r.GetString(1), employeeId = r.GuidOrNull(2), name = r.GetString(3), addedBy = r.GetString(4), addedAt = r.GetFieldValue<DateTime>(5) }, ct, Tenant));
    }

    public record MemberInput(Guid EmployeeId);

    [HttpPost("committee")]
    public async Task<IActionResult> AddMember([FromBody] MemberInput body, CancellationToken ct)
    {
        if (!CanManage) return StatusCode(403, new { message = L("Etik kurulunu yalnızca şirket yöneticisi düzenler.", "Only the tenant admin manages the committee.") });
        var p = await People.FindAsync(Tenant, body.EmployeeId, ct);
        if (p is null) return NotFound(new { message = L("Çalışan bulunamadı.", "Employee not found.") });
        if (p.UserId is null) return BadRequest(new { message = L("Çalışanın kullanıcı hesabı yok; önce giriş erişimi verin.", "Employee has no user account.") });
        var id = Guid.NewGuid();
        var n = await Db.ExecuteAsync("""
            INSERT INTO governance_ethics_committee ("Id","TenantSlug","UserId","EmployeeId","Name","AddedBy","AddedAt")
            VALUES ($1,$2,$3,$4,$5,$6,now()) ON CONFLICT ("TenantSlug","UserId") DO NOTHING
            """, ct, id, Tenant, p.UserId, p.Id, p.Name, Me.Name);
        if (n == 0) return Conflict(new { message = L("Bu kişi zaten kurulda.", "Already a member.") });
        await ComplianceAudit.WriteAsync(Db, Tenant, "EthicsCommittee", p.Id.ToString(), "Added", new { member = p.Name }, Me.UserId, Me.Name, ct);
        return Ok(new { id });
    }

    [HttpDelete("committee/{id:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, CancellationToken ct)
    {
        if (!CanManage) return StatusCode(403, new { message = L("Etik kurulunu yalnızca şirket yöneticisi düzenler.", "Only the tenant admin manages the committee.") });
        var name = (await Db.QueryAsync("DELETE FROM governance_ethics_committee WHERE \"TenantSlug\" = $1 AND \"Id\" = $2 RETURNING \"Name\"", r => r.GetString(0), ct, Tenant, id)).FirstOrDefault();
        if (name is null) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "EthicsCommittee", id.ToString(), "Removed", new { member = name }, Me.UserId, Me.Name, ct);
        return NoContent();
    }

    /* -------------------------------------------------------------- bildirimler */

    [HttpGet("reports")]
    public async Task<IActionResult> Reports([FromQuery] string? status, CancellationToken ct)
    {
        if (!await IsMemberAsync(ct)) return NotMember();
        var rows = await Db.QueryAsync("""
            SELECT r."Id", r."Category", r."Status", r."ReceivedOn", r."UpdatedAt", r."ContactEnc" IS NOT NULL,
                   (SELECT count(*) FROM governance_ethics_messages m WHERE m."ReportId" = r."Id"),
                   (SELECT m."FromReporter" FROM governance_ethics_messages m WHERE m."ReportId" = r."Id" ORDER BY m."Seq" DESC LIMIT 1)
            FROM governance_ethics_reports r WHERE r."TenantSlug" = $1 AND ($2::text IS NULL OR r."Status" = $2)
            ORDER BY r."Status" = 'Closed', r."ReceivedOn" DESC LIMIT 500
            """, r => new
            {
                id = r.GetGuid(0), category = r.GetString(1), categoryLabel = EthicsCode.Categories.GetValueOrDefault(r.GetString(1), r.GetString(1)),
                status = r.GetString(2), receivedOn = r.GetFieldValue<DateOnly>(3), updatedAt = r.GetFieldValue<DateTime>(4), hasContact = r.GetBoolean(5),
                messages = Convert.ToInt32(r.GetValue(6)),
                awaitingReply = r.IsDBNull(7) ? r.GetString(2) == "Received" : r.GetBoolean(7) && r.GetString(2) != "Closed",
            }, ct, Tenant, status);
        return Ok(rows);
    }

    [HttpGet("reports/{id:guid}")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(ct)) return NotMember();
        var head = (await Db.QueryAsync("""
            SELECT "Id","Category","Description","Status","Outcome","ReceivedOn","ContactEnc" IS NOT NULL,"ClosedAt"
            FROM governance_ethics_reports WHERE "TenantSlug" = $1 AND "Id" = $2
            """, r => new
            {
                id = r.GetGuid(0), category = r.GetString(1), categoryLabel = EthicsCode.Categories.GetValueOrDefault(r.GetString(1), r.GetString(1)),
                description = r.GetString(2), status = r.GetString(3), outcome = r.Str(4), receivedOn = r.GetFieldValue<DateOnly>(5),
                hasContact = r.GetBoolean(6), closedAt = r.Ts(7),
            }, ct, Tenant, id)).FirstOrDefault();
        if (head is null) return NotFound();
        var messages = await Db.QueryAsync("""
            SELECT "FromReporter","Author","Body","CreatedOn" FROM governance_ethics_messages WHERE "TenantSlug" = $1 AND "ReportId" = $2 ORDER BY "Seq"
            """, r => new { fromReporter = r.GetBoolean(0), author = r.Str(1), body = r.GetString(2), createdOn = r.GetFieldValue<DateOnly>(3) }, ct, Tenant, id);
        await ComplianceAudit.WriteAsync(Db, Tenant, "EthicsReport", id.ToString(), "SensitiveViewed", new { field = "report" }, Me.UserId, Me.Name, ct);
        return Ok(new { head.id, head.category, head.categoryLabel, head.description, head.status, head.outcome, head.receivedOn, head.hasContact, head.closedAt, messages });
    }

    /// <summary>İhbarcının isteğe bağlı bıraktığı iletişim bilgisi; her açılış erişim kaydına yazılır.</summary>
    [HttpGet("reports/{id:guid}/contact")]
    public async Task<IActionResult> Contact(Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(ct)) return NotMember();
        var enc = (await Db.QueryAsync("SELECT \"ContactEnc\" FROM governance_ethics_reports WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", r => r.Str(0), ct, Tenant, id)).FirstOrDefault();
        if (enc is null) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "EthicsReport", id.ToString(), "Revealed", new { field = "contact" }, Me.UserId, Me.Name, ct);
        return Ok(new { contact = SecretBox.Unprotect(enc) });
    }

    public record StatusInput(string Status, string? Outcome);

    [HttpPost("reports/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] StatusInput body, CancellationToken ct)
    {
        if (!await IsMemberAsync(ct)) return NotMember();
        if (!EthicsCode.Statuses.Contains(body.Status)) return BadRequest(new { message = L("Geçersiz durum.", "Invalid status.") });
        var outcome = string.IsNullOrWhiteSpace(body.Outcome) ? null : body.Outcome.Trim()[..Math.Min(body.Outcome.Trim().Length, 2000)];
        var n = await Db.ExecuteAsync("""
            UPDATE governance_ethics_reports SET "Status" = $3, "Outcome" = coalesce($4, "Outcome"), "UpdatedAt" = now(),
                "ClosedAt" = CASE WHEN $3 = 'Closed' THEN coalesce("ClosedAt", now()) ELSE NULL END
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, body.Status, outcome);
        if (n == 0) return NotFound();
        await ComplianceAudit.WriteAsync(Db, Tenant, "EthicsReport", id.ToString(), "Updated", new { status = body.Status }, Me.UserId, Me.Name, ct);
        return Ok(new { id, status = body.Status });
    }

    public record ReplyInput(string Body);

    /// <summary>Kurul yanıtı; ihbarcı "Etik Kurulu" imzasını görür (üyenin adı gösterilmez).</summary>
    [HttpPost("reports/{id:guid}/messages")]
    public async Task<IActionResult> Reply(Guid id, [FromBody] ReplyInput body, CancellationToken ct)
    {
        if (!await IsMemberAsync(ct)) return NotMember();
        var text = body.Body?.Trim() ?? "";
        if (text.Length is < 1 or > 5000) return BadRequest(new { message = L("Mesaj 1-5000 karakter olmalı.", "Message must be 1-5000 characters.") });
        var status = (await Db.QueryAsync("SELECT \"Status\" FROM governance_ethics_reports WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", r => r.GetString(0), ct, Tenant, id)).FirstOrDefault();
        if (status is null) return NotFound();
        if (status == "Closed") return Conflict(new { message = L("Kapatılmış bildirime yanıt yazılamaz.", "Report is closed.") });
        await Db.ExecuteAsync("""
            INSERT INTO governance_ethics_messages ("Id","TenantSlug","ReportId","FromReporter","Author","Body","CreatedOn")
            VALUES ($1,$2,$3,false,'Etik Kurulu',$4,current_date)
            """, ct, Guid.NewGuid(), Tenant, id, text);
        await Db.ExecuteAsync("""
            UPDATE governance_ethics_reports SET "Status" = CASE WHEN "Status" = 'Received' THEN 'InReview' ELSE "Status" END, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id);
        await ComplianceAudit.WriteAsync(Db, Tenant, "EthicsReport", id.ToString(), "Replied", new { }, Me.UserId, Me.Name, ct);
        return Ok(new { sent = true });
    }
}
