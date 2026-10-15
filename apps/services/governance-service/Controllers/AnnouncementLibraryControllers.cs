using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Y14 Duyurular + G19 Doküman kütüphanesi. İkisi de aynı "okudum / kabul
 * ettim" kayıt tablosunu (governance_acknowledgements) kullanır. Bu kayıt
 * bir RIZA değildir; KVKK açık rıza kayıtları governance_consents'te ayrı tutulur.
 * ==================================================================== */

/// <summary>Okudum/kabul kayıtları için ortak yardımcılar.</summary>
public static class Acks
{
    public const string Announcement = "Announcement", LibraryDocument = "LibraryDocument";

    public static async Task<bool> AddAsync(Sql sql, string tenant, string type, Guid subject, int version, UserInfo me, Person? person, CancellationToken ct) =>
        await sql.ExecuteAsync("""
            INSERT INTO governance_acknowledgements ("Id","TenantSlug","SubjectType","SubjectId","Version","UserId","EmployeeId","PersonName","AcknowledgedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,now())
            ON CONFLICT ("TenantSlug","SubjectType","SubjectId","Version","UserId") DO NOTHING
            """, ct, Guid.NewGuid(), tenant, type, subject, version, me.UserId, person?.Id, person?.Name ?? me.Name) > 0;

    public sealed record AckRow(Guid SubjectId, int Version, string UserId, Guid? EmployeeId, string PersonName, DateTime At);

    public static Task<List<AckRow>> ListAsync(Sql sql, string tenant, string type, Guid? subject, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT "SubjectId","Version","UserId","EmployeeId","PersonName","AcknowledgedAt" FROM governance_acknowledgements
            WHERE "TenantSlug" = $1 AND "SubjectType" = $2 AND ($3::uuid IS NULL OR "SubjectId" = $3)
            """, r => new AckRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GuidOrNull(3), r.GetString(4), r.GetFieldValue<DateTime>(5)),
            ct, tenant, type, subject);

    public static Task<List<AckRow>> MineAsync(Sql sql, string tenant, string type, string userId, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT "SubjectId","Version","UserId","EmployeeId","PersonName","AcknowledgedAt" FROM governance_acknowledgements
            WHERE "TenantSlug" = $1 AND "SubjectType" = $2 AND "UserId" = $3
            """, r => new AckRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GuidOrNull(3), r.GetString(4), r.GetFieldValue<DateTime>(5)),
            ct, tenant, type, userId);

    /// <summary>Kitle içindeki okuma istatistiği; okumayanlar listesi yalnızca istenirse.</summary>
    public static object Stats(List<Person>? audience, IEnumerable<AckRow> acks, bool listMissing)
    {
        var list = acks.ToList();
        if (audience is null)
            return new { total = (int?)null, read = list.Count, notRead = Array.Empty<object>() };
        var withAccount = audience.Where(p => p.UserId is not null).ToList();
        bool Read(Person p) => list.Any(a => a.EmployeeId == p.Id || a.UserId == p.UserId);
        var readCount = withAccount.Count(Read);
        return new
        {
            total = (int?)withAccount.Count,
            read = readCount,
            notRead = listMissing
                ? withAccount.Where(p => !Read(p)).Select(p => (object)new { employeeId = p.Id, name = p.Name, department = p.Department }).ToArray()
                : Array.Empty<object>(),
        };
    }
}

/// <summary>Y14 — duyurular.</summary>
[Route("api/announcements")]
[Authorize]
public class AnnouncementsController : AppController
{
    private readonly Notifier _notifier;
    public AnnouncementsController(Notifier notifier) => _notifier = notifier;

    public sealed record Row(Guid Id, string Title, string Body, string Audience, Guid[] DepartmentIds, DateTime PublishAt,
        DateTime? ExpireAt, bool RequiresAck, DateTime? NotifiedAt, string CreatedBy, DateTime CreatedAt);

    private const string Cols = "\"Id\",\"Title\",\"Body\",\"Audience\",\"DepartmentIds\",\"PublishAt\",\"ExpireAt\",\"RequiresAck\",\"NotifiedAt\",\"CreatedBy\",\"CreatedAt\"";
    private static Row Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<Guid[]>(4),
        r.GetFieldValue<DateTime>(5), r.Ts(6), r.GetBoolean(7), r.Ts(8), r.GetString(9), r.GetFieldValue<DateTime>(10));

    /// <summary>
    /// Yayım zamanı gelmiş ama bildirimi gönderilmemiş duyuruların kitlesine uygulama içi bildirim
    /// (yalnızca başlık). Liste açılışında ve saatlik bakımda çağrılır; her duyuru bir kez.
    /// </summary>
    public static async Task<int> PublishDueAsync(Sql sql, PeopleDirectory people, string? onlyTenant, CancellationToken ct)
    {
        var due = await sql.QueryAsync($"""
            UPDATE governance_announcements SET "NotifiedAt" = now()
            WHERE "NotifiedAt" IS NULL AND "PublishAt" <= now() AND ("ExpireAt" IS NULL OR "ExpireAt" > now())
              AND ($1::text IS NULL OR "TenantSlug" = $1)
            RETURNING "TenantSlug","Title","Audience","DepartmentIds"
            """, r => (Tenant: r.GetString(0), Title: r.GetString(1), Audience: r.GetString(2), Depts: r.GetFieldValue<Guid[]>(3)), ct, onlyTenant);
        var sent = 0;
        foreach (var a in due)
        {
            var all = await people.ListAsync(a.Tenant, ct);
            var members = Audience.Members(a.Audience, a.Depts, all, new HashSet<Guid>()) ?? new List<Person>();
            // G2: başlık İK'nın yazdığı metindir (olduğu gibi); sistem metni alıcının dilinde.
            sent += await BulkNotifyLocalized.InAppAsync(sql, a.Tenant, members.Select(p => p.Id), a.Title, a.Title,
                "Yeni duyuru: Duyurular sayfasından okuyabilirsiniz.", "New announcement: you can read it on the Announcements page.", "announcement.published", ct);
        }
        return sent;
    }

    private bool Visible(Row a, Person? me) =>
        a.Audience == Audience.All || (a.Audience == Audience.Departments && me?.DepartmentId is { } d && a.DepartmentIds.Contains(d));

    /// <summary>Kullanıcıya yönelik yayımdaki duyurular (İK da yalnızca kendine yönelik olanları burada görür).</summary>
    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        await PublishDueAsync(Db, People, Tenant, ct);
        var me = await MyPersonAsync(ct);
        var rows = await Db.QueryAsync($"""
            SELECT {Cols} FROM governance_announcements
            WHERE "TenantSlug" = $1 AND "PublishAt" <= now() AND ("ExpireAt" IS NULL OR "ExpireAt" > now())
            ORDER BY "PublishAt" DESC LIMIT 200
            """, Map, ct, Tenant);
        var acks = (await Acks.MineAsync(Db, Tenant, Acks.Announcement, Me.UserId, ct)).ToDictionary(a => a.SubjectId, a => a.At);
        return Ok(rows.Where(a => Visible(a, me)).Select(a => new
        {
            a.Id, a.Title, a.Body, a.PublishAt, a.ExpireAt, a.RequiresAck,
            read = acks.ContainsKey(a.Id), readAt = acks.TryGetValue(a.Id, out var at) ? at : (DateTime?)null,
        }));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var a = (await Db.QueryAsync($"""
            SELECT {Cols} FROM governance_announcements WHERE "TenantSlug" = $1 AND "Id" = $2
              AND "PublishAt" <= now() AND ("ExpireAt" IS NULL OR "ExpireAt" > now())
            """, Map, ct, Tenant, id)).FirstOrDefault();
        var me = await MyPersonAsync(ct);
        if (a is null || !Visible(a, me)) return NotFound(new { message = L("Duyuru bulunamadı.", "Announcement not found.") });
        await Acks.AddAsync(Db, Tenant, Acks.Announcement, id, 0, Me, me, ct);
        return Ok(new { read = true });
    }

    /* -------------------------------------------------------------- İK yönetimi */

    [HttpGet("manage")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Manage(CancellationToken ct)
    {
        var rows = await Db.QueryAsync($"SELECT {Cols} FROM governance_announcements WHERE \"TenantSlug\" = $1 ORDER BY \"PublishAt\" DESC LIMIT 300", Map, ct, Tenant);
        var people = await People.ListAsync(Tenant, ct);
        var acks = (await Acks.ListAsync(Db, Tenant, Acks.Announcement, null, ct)).ToLookup(a => a.SubjectId);
        var now = DateTime.UtcNow;
        return Ok(rows.Select(a => new
        {
            a.Id, a.Title, a.Body, a.Audience, a.DepartmentIds, a.PublishAt, a.ExpireAt, a.RequiresAck, a.NotifiedAt, a.CreatedBy, a.CreatedAt,
            state = a.PublishAt > now ? "Scheduled" : a.ExpireAt is { } e && e <= now ? "Expired" : "Published",
            stats = Acks.Stats(Audience.Members(a.Audience, a.DepartmentIds, people, new HashSet<Guid>()), acks[a.Id], listMissing: false),
        }));
    }

    /// <summary>Okuma istatistiği; okumayanlar listesi yalnızca onay gerektiren duyurularda.</summary>
    [HttpGet("{id:guid}/stats")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct)
    {
        var a = (await Db.QueryAsync($"SELECT {Cols} FROM governance_announcements WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", Map, ct, Tenant, id)).FirstOrDefault();
        if (a is null) return NotFound();
        var people = await People.ListAsync(Tenant, ct);
        var acks = await Acks.ListAsync(Db, Tenant, Acks.Announcement, id, ct);
        return Ok(Acks.Stats(Audience.Members(a.Audience, a.DepartmentIds, people, new HashSet<Guid>()), acks, listMissing: a.RequiresAck));
    }

    public record AnnouncementInput(string Title, string Body, string? Audience, Guid[]? DepartmentIds, DateTime? PublishAt, DateTime? ExpireAt, bool RequiresAck);

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] AnnouncementInput body, CancellationToken ct)
    {
        var title = body.Title?.Trim() ?? "";
        var text = body.Body?.Trim() ?? "";
        var audience = body.Audience ?? Audience.All;
        var depts = body.DepartmentIds?.Distinct().ToArray() ?? Array.Empty<Guid>();
        if (title.Length is < 3 or > 200) return BadRequest(new { message = L("Başlık 3-200 karakter olmalı.", "Title must be 3-200 characters.") });
        if (text.Length is < 1 or > 20000) return BadRequest(new { message = L("Duyuru metni 1-20000 karakter olmalı.", "Body must be 1-20000 characters.") });
        if (!Audience.AnnouncementAudiences.Contains(audience)) return BadRequest(new { message = L("Geçersiz hedef kitle.", "Invalid audience.") });
        if (audience == Audience.Departments && depts.Length == 0) return BadRequest(new { message = L("En az bir departman seçin.", "Select at least one department.") });
        if (audience == Audience.All) depts = Array.Empty<Guid>();
        var publishAt = body.PublishAt?.ToUniversalTime() ?? DateTime.UtcNow;
        if (body.ExpireAt is { } exp && exp.ToUniversalTime() <= publishAt)
            return BadRequest(new { message = L("Bitiş tarihi yayım tarihinden sonra olmalı.", "Expiry must be after publish date.") });
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_announcements ("Id","TenantSlug","Title","Body","Audience","DepartmentIds","PublishAt","ExpireAt","RequiresAck","CreatedBy","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now(),now())
            """, ct, id, Tenant, title, text, audience, depts, publishAt, body.ExpireAt?.ToUniversalTime(), body.RequiresAck, Me.Name);
        var notified = await PublishDueAsync(Db, People, Tenant, ct);
        return Ok(new { id, notified });
    }

    /// <summary>
    /// Planlı ya da yayımdaki duyuruyu düzenler (başlık, metin, hedef kitle, yayım/bitiş zamanı,
    /// onay gereksinimi). Okuma kayıtları korunur. Yayım zamanı verilmezse mevcut zaman kalır;
    /// geleceğe alınırsa bildirim o zaman yeniden gönderilir.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] AnnouncementInput body, CancellationToken ct)
    {
        var current = (await Db.QueryAsync($"SELECT {Cols} FROM governance_announcements WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", Map, ct, Tenant, id)).FirstOrDefault();
        if (current is null) return NotFound(new { message = L("Duyuru bulunamadı.", "Announcement not found.") });
        var title = body.Title?.Trim() ?? "";
        var text = body.Body?.Trim() ?? "";
        var audience = body.Audience ?? Audience.All;
        var depts = body.DepartmentIds?.Distinct().ToArray() ?? Array.Empty<Guid>();
        if (title.Length is < 3 or > 200) return BadRequest(new { message = L("Başlık 3-200 karakter olmalı.", "Title must be 3-200 characters.") });
        if (text.Length is < 1 or > 20000) return BadRequest(new { message = L("Duyuru metni 1-20000 karakter olmalı.", "Body must be 1-20000 characters.") });
        if (!Audience.AnnouncementAudiences.Contains(audience)) return BadRequest(new { message = L("Geçersiz hedef kitle.", "Invalid audience.") });
        if (audience == Audience.Departments && depts.Length == 0) return BadRequest(new { message = L("En az bir departman seçin.", "Select at least one department.") });
        if (audience == Audience.All) depts = Array.Empty<Guid>();
        var publishAt = body.PublishAt?.ToUniversalTime() ?? DateTime.SpecifyKind(current.PublishAt, DateTimeKind.Utc);
        if (body.ExpireAt is { } exp && exp.ToUniversalTime() <= publishAt)
            return BadRequest(new { message = L("Bitiş tarihi yayım tarihinden sonra olmalı.", "Expiry must be after publish date.") });
        // Yayım geleceğe alındıysa bildirim o zaman (yeniden) gönderilsin.
        var resetNotified = publishAt > DateTime.UtcNow;
        await Db.ExecuteAsync("""
            UPDATE governance_announcements SET "Title" = $3, "Body" = $4, "Audience" = $5, "DepartmentIds" = $6, "PublishAt" = $7,
                "ExpireAt" = $8, "RequiresAck" = $9, "NotifiedAt" = CASE WHEN $10 THEN NULL ELSE "NotifiedAt" END, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, title, text, audience, depts, publishAt, body.ExpireAt?.ToUniversalTime(), body.RequiresAck, resetNotified);
        await ComplianceAudit.WriteAsync(Db, Tenant, "Announcement", id.ToString(), "Updated",
            new { title, audience, publishAt, expireAt = body.ExpireAt?.ToUniversalTime(), body.RequiresAck }, Me.UserId, Me.Name, ct);
        var notified = await PublishDueAsync(Db, People, Tenant, ct);
        return Ok(new { id, notified });
    }

    /// <summary>Duyuruyu hemen yayından kaldırır (okuma kayıtları kalır).</summary>
    [HttpPost("{id:guid}/expire")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Expire(Guid id, CancellationToken ct)
    {
        var n = await Db.ExecuteAsync("UPDATE governance_announcements SET \"ExpireAt\" = now(), \"UpdatedAt\" = now() WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        return n == 0 ? NotFound() : Ok(new { expired = true });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await Db.ExecuteAsync("DELETE FROM governance_acknowledgements WHERE \"TenantSlug\" = $1 AND \"SubjectType\" = 'Announcement' AND \"SubjectId\" = $2", ct, Tenant, id);
        var n = await Db.ExecuteAsync("DELETE FROM governance_announcements WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, Tenant, id);
        if (n > 0) await ComplianceAudit.WriteAsync(Db, Tenant, "Announcement", id.ToString(), "Deleted", new { }, Me.UserId, Me.Name, ct);
        return n == 0 ? NotFound() : NoContent();
    }

    /// <summary>Hedef kitle seçimi için departmanlar (yalnızca ad).</summary>
    [HttpGet("departments")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Departments(CancellationToken ct) =>
        Ok(await Db.QueryAsync("SELECT \"Id\", \"Name\" FROM organization_departments WHERE \"TenantSlug\" = $1 ORDER BY 2",
            r => new { id = r.GetGuid(0), name = r.GetString(1) }, ct, Tenant));
}

/// <summary>G19 — sürümlü doküman kütüphanesi ve tam metin arama.</summary>
[Route("api/library")]
[Authorize]
public class LibraryController : AppController
{
    public static readonly Dictionary<string, string> Categories = new()
    {
        ["Policy"] = "Politika", ["Handbook"] = "El kitabı", ["Procedure"] = "Prosedür", ["Form"] = "Form", ["Other"] = "Diğer",
    };

    public sealed record Doc(Guid Id, string Title, string Category, string Audience, Guid[] DepartmentIds, bool RequiresAck,
        Guid? CurrentVersionId, int CurrentVersionNo, bool Archived, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

    private const string Cols = "d.\"Id\",d.\"Title\",d.\"Category\",d.\"Audience\",d.\"DepartmentIds\",d.\"RequiresAck\",d.\"CurrentVersionId\",d.\"CurrentVersionNo\",d.\"Archived\",d.\"CreatedBy\",d.\"CreatedAt\",d.\"UpdatedAt\"";
    private static Doc Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<Guid[]>(4),
        r.GetBoolean(5), r.GuidOrNull(6), r.GetInt32(7), r.GetBoolean(8), r.GetString(9), r.GetFieldValue<DateTime>(10), r.GetFieldValue<DateTime>(11));

    /// <summary>Yetki süzgecinin SQL hâli (arama ve liste aynı kuralı kullanır; bkz. Audience.CanSee).</summary>
    private const string VisibleSql = """
        ($2 OR (NOT d."Archived" AND (d."Audience" = 'All' OR (d."Audience" = 'Managers' AND $3)
              OR (d."Audience" = 'Departments' AND $4::uuid IS NOT NULL AND $4::uuid = ANY(d."DepartmentIds")))))
        """;

    private bool IsManagerRole => Me.IsManager;

    private async Task<(bool Hr, bool Mgr, Guid? Dept, Person? Me)> ScopeAsync(CancellationToken ct)
    {
        var p = await MyPersonAsync(ct);
        return (Me.IsHr, IsManagerRole, p?.DepartmentId, p);
    }

    private async Task<Doc?> FindVisibleAsync(Guid id, CancellationToken ct)
    {
        var s = await ScopeAsync(ct);
        return (await Db.QueryAsync($"SELECT {Cols} FROM governance_library_documents d WHERE d.\"TenantSlug\" = $1 AND d.\"Id\" = $5 AND {VisibleSql}",
            Map, ct, Tenant, s.Hr, s.Mgr, s.Dept, id)).FirstOrDefault();
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? category, CancellationToken ct)
    {
        var s = await ScopeAsync(ct);
        var docs = await Db.QueryAsync($"""
            SELECT {Cols}, v."PublishedAt", v."ExternalUrl" IS NOT NULL OR v."StorageKey" IS NOT NULL
            FROM governance_library_documents d LEFT JOIN governance_library_versions v ON v."Id" = d."CurrentVersionId"
            WHERE d."TenantSlug" = $1 AND {VisibleSql} AND ($5::text IS NULL OR d."Category" = $5)
            ORDER BY d."Category", d."Title" LIMIT 500
            """, r => (Doc: Map(r), PublishedAt: r.Ts(12), HasFile: !r.IsDBNull(13) && r.GetBoolean(13)), ct, Tenant, s.Hr, s.Mgr, s.Dept, category);
        var mine = await Acks.MineAsync(Db, Tenant, Acks.LibraryDocument, Me.UserId, ct);
        return Ok(docs.Select(x =>
        {
            var acked = mine.Where(a => a.SubjectId == x.Doc.Id).OrderByDescending(a => a.Version).FirstOrDefault();
            return new
            {
                x.Doc.Id, x.Doc.Title, x.Doc.Category, categoryLabel = Categories.GetValueOrDefault(x.Doc.Category, x.Doc.Category),
                x.Doc.Audience, x.Doc.DepartmentIds, x.Doc.RequiresAck, version = x.Doc.CurrentVersionNo, x.Doc.Archived, publishedAt = x.PublishedAt, hasFile = x.HasFile,
                acknowledgedVersion = acked?.Version, acknowledgedAt = acked?.At,
                needsAck = x.Doc.RequiresAck && !x.Doc.Archived && x.Doc.CurrentVersionNo > 0 && acked?.Version != x.Doc.CurrentVersionNo,
            };
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var d = await FindVisibleAsync(id, ct);
        if (d is null) return NotFound(new { message = L("Belge bulunamadı.", "Document not found.") });
        var versions = await Db.QueryAsync("""
            SELECT "Id","VersionNo","Title","Body","ExternalUrl","StorageKey","ChangeNote","PublishedBy","PublishedAt"
            FROM governance_library_versions WHERE "TenantSlug" = $1 AND "DocumentId" = $2 ORDER BY "VersionNo" DESC
            """, r => new
            {
                id = r.GetGuid(0), versionNo = r.GetInt32(1), title = r.GetString(2), body = r.GetString(3), externalUrl = r.Str(4),
                storageKey = r.Str(5), changeNote = r.Str(6), publishedBy = r.GetString(7), publishedAt = r.GetFieldValue<DateTime>(8),
            }, ct, Tenant, id);
        var current = versions.FirstOrDefault(v => v.id == d.CurrentVersionId);
        var mine = (await Acks.MineAsync(Db, Tenant, Acks.LibraryDocument, Me.UserId, ct)).Where(a => a.SubjectId == id).OrderByDescending(a => a.Version).FirstOrDefault();
        return Ok(new
        {
            d.Id, d.Title, d.Category, categoryLabel = Categories.GetValueOrDefault(d.Category, d.Category), d.Audience, d.DepartmentIds, d.RequiresAck, d.Archived,
            version = d.CurrentVersionNo, current,
            // Eski sürümlerin metni yalnızca İK'ya; çalışan geçmişi (no, tarih, değişiklik notu) görür.
            history = versions.Select(v => new { v.versionNo, v.publishedAt, v.publishedBy, v.changeNote }),
            acknowledgedVersion = mine?.Version, acknowledgedAt = mine?.At,
            needsAck = d.RequiresAck && !d.Archived && d.CurrentVersionNo > 0 && mine?.Version != d.CurrentVersionNo,
        });
    }

    [HttpGet("{id:guid}/versions/{no:int}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Version(Guid id, int no, CancellationToken ct)
    {
        var v = (await Db.QueryAsync("""
            SELECT "VersionNo","Title","Body","ExternalUrl","StorageKey","ChangeNote","PublishedBy","PublishedAt"
            FROM governance_library_versions WHERE "TenantSlug" = $1 AND "DocumentId" = $2 AND "VersionNo" = $3
            """, r => new
            {
                versionNo = r.GetInt32(0), title = r.GetString(1), body = r.GetString(2), externalUrl = r.Str(3), storageKey = r.Str(4),
                changeNote = r.Str(5), publishedBy = r.GetString(6), publishedAt = r.GetFieldValue<DateTime>(7),
            }, ct, Tenant, id, no)).FirstOrDefault();
        return v is null ? NotFound() : Ok(v);
    }

    /// <summary>
    /// Tam metin arama (PostgreSQL FTS, 'simple' sözlüğü, ı/i katlamalı). Yalnızca güncel sürümde
    /// ve YALNIZCA kullanıcının görebileceği belgelerde arar; yetkisiz belge hiçbir şekilde dönmez.
    /// Parçacıkta eşleşmeler ⟦ ⟧ ile işaretlenir (HTML değil — arayüz güvenle vurgular).
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Ok(Array.Empty<object>());
        if (q.Length > 200) return BadRequest(new { message = L("Arama ifadesi en fazla 200 karakter olabilir.", "Query too long.") });
        var s = await ScopeAsync(ct);
        var rows = await Db.QueryAsync($"""
            WITH q AS (SELECT websearch_to_tsquery('simple', translate(lower($5), 'ı', 'i')) AS fq,
                              websearch_to_tsquery('simple', translate(lower($5), 'ı', 'i')) || websearch_to_tsquery('simple', lower($5)) AS hq,
                              -- Aksan duyarsız eşleşme (çalışan listesiyle aynı hr360_fold): "ayse"/"AYSE" → "Ayşe".
                              websearch_to_tsquery('simple', hr360_fold($5)) AS foldq)
            SELECT d."Id", d."Title", d."Category", d."CurrentVersionNo",
                   ts_headline('simple', v."Body", q.hq, 'StartSel="⟦", StopSel="⟧", MaxWords=35, MinWords=12, MaxFragments=2, FragmentDelimiter=" … "'),
                   ts_rank(v."SearchVector", q.fq) AS rank, d."RequiresAck"
            FROM governance_library_documents d
            JOIN governance_library_versions v ON v."Id" = d."CurrentVersionId"
            CROSS JOIN q
            WHERE d."TenantSlug" = $1
              AND (v."SearchVector" @@ q.fq
                   OR to_tsvector('simple', hr360_fold(coalesce(v."Title", '') || ' ' || coalesce(v."Body", ''))) @@ q.foldq)
              AND {VisibleSql}
            ORDER BY rank DESC, d."Title" LIMIT 30
            """, r => new
            {
                id = r.GetGuid(0), title = r.GetString(1), category = r.GetString(2), categoryLabel = Categories.GetValueOrDefault(r.GetString(2), r.GetString(2)),
                version = r.GetInt32(3), snippet = r.GetString(4), rank = r.GetFloat(5), requiresAck = r.GetBoolean(6),
            }, ct, Tenant, s.Hr, s.Mgr, s.Dept, q);
        return Ok(rows);
    }

    public record DocInput(string Title, string? Category, string? Audience, Guid[]? DepartmentIds, bool RequiresAck,
        string? Body, string? ExternalUrl, string? StorageKey, string? ChangeNote);

    private string? Validate(string title, string category, string audience, Guid[] depts)
    {
        if (title.Length is < 3 or > 200) return L("Başlık 3-200 karakter olmalı.", "Title must be 3-200 characters.");
        if (!Categories.ContainsKey(category)) return L("Geçersiz kategori.", "Invalid category.");
        if (!Audience.DocumentAudiences.Contains(audience)) return L("Geçersiz hedef kitle.", "Invalid audience.");
        if (audience == Audience.Departments && depts.Length == 0) return L("En az bir departman seçin.", "Select at least one department.");
        return null;
    }

    private string? ValidateVersion(string body, string? url, string? key)
    {
        if (body.Length > 200_000) return L("Metin en fazla 200.000 karakter olabilir.", "Body too long.");
        if (body.Length == 0 && string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key))
            return L("Belge metni ya da dosya bağlantısı gerekli.", "Body or file link is required.");
        if (!string.IsNullOrWhiteSpace(url) && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")))
            return L("Dosya bağlantısı http(s) adresi olmalı.", "File link must be an http(s) URL.");
        if (key is { Length: > 300 }) return L("Depolama anahtarı çok uzun.", "Storage key too long.");
        return null;
    }

    private async Task<Guid> AddVersionAsync(Guid docId, int no, string title, string body, string? url, string? key, string? note, CancellationToken ct)
    {
        var vid = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_library_versions ("Id","TenantSlug","DocumentId","VersionNo","Title","Body","ExternalUrl","StorageKey","ChangeNote","PublishedBy","PublishedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now())
            """, ct, vid, Tenant, docId, no, title, body, string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
            string.IsNullOrWhiteSpace(key) ? null : key.Trim(), string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)], Me.Name);
        await Db.ExecuteAsync("""
            UPDATE governance_library_documents SET "CurrentVersionId" = $3, "CurrentVersionNo" = $4, "Title" = $5, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, docId, vid, no, title);
        return vid;
    }

    private async Task NotifyAckAsync(Doc d, string subject, string subjectEn, CancellationToken ct)
    {
        if (!d.RequiresAck || d.Archived || d.Audience == Audience.Hr) return;
        var people = await People.ListAsync(Tenant, ct);
        var leaders = d.Audience == Audience.Managers ? await Audience.LeadersAsync(Db, Tenant, ct) : new HashSet<Guid>();
        var members = Audience.Members(d.Audience, d.DepartmentIds, people, leaders) ?? new List<Person>();
        await BulkNotifyLocalized.InAppAsync(Db, Tenant, members.Select(p => p.Id), subject, subjectEn,
            "Doküman kütüphanesinden okuyup onaylamanız bekleniyor.", "Please read and acknowledge it in the document library.", "library.ack", ct);
    }

    [HttpPost]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Create([FromBody] DocInput body, CancellationToken ct)
    {
        var title = body.Title?.Trim() ?? "";
        var category = body.Category ?? "Policy";
        var audience = body.Audience ?? Audience.All;
        var depts = audience == Audience.Departments ? body.DepartmentIds?.Distinct().ToArray() ?? Array.Empty<Guid>() : Array.Empty<Guid>();
        var text = body.Body?.Trim() ?? "";
        if ((Validate(title, category, audience, depts) ?? ValidateVersion(text, body.ExternalUrl, body.StorageKey)) is { } err) return BadRequest(new { message = err });
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("""
            INSERT INTO governance_library_documents ("Id","TenantSlug","Title","Category","Audience","DepartmentIds","RequiresAck","CreatedBy","CreatedAt","UpdatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,now(),now())
            """, ct, id, Tenant, title, category, audience, depts, body.RequiresAck, Me.Name);
        await AddVersionAsync(id, 1, title, text, body.ExternalUrl, body.StorageKey, body.ChangeNote ?? "İlk sürüm", ct);
        var d = (await Db.QueryAsync($"SELECT {Cols} FROM governance_library_documents d WHERE d.\"TenantSlug\" = $1 AND d.\"Id\" = $2", Map, ct, Tenant, id)).First();
        await NotifyAckAsync(d, $"Okumanız gereken yeni belge: {title}", $"New document to read: {title}", ct);
        return Ok(new { id, version = 1 });
    }

    public record VersionInput(string? Title, string? Body, string? ExternalUrl, string? StorageKey, string? ChangeNote);

    /// <summary>Yeni sürüm: eski sürümler silinmez; onay gerektiren belgede herkesin yeniden onaylaması gerekir.</summary>
    [HttpPost("{id:guid}/versions")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> NewVersion(Guid id, [FromBody] VersionInput body, CancellationToken ct)
    {
        var d = (await Db.QueryAsync($"SELECT {Cols} FROM governance_library_documents d WHERE d.\"TenantSlug\" = $1 AND d.\"Id\" = $2", Map, ct, Tenant, id)).FirstOrDefault();
        if (d is null) return NotFound();
        var title = string.IsNullOrWhiteSpace(body.Title) ? d.Title : body.Title.Trim();
        var text = body.Body?.Trim() ?? "";
        if (title.Length is < 3 or > 200) return BadRequest(new { message = L("Başlık 3-200 karakter olmalı.", "Title must be 3-200 characters.") });
        if (ValidateVersion(text, body.ExternalUrl, body.StorageKey) is { } err) return BadRequest(new { message = err });
        var no = d.CurrentVersionNo + 1;
        await AddVersionAsync(id, no, title, text, body.ExternalUrl, body.StorageKey, body.ChangeNote, ct);
        await NotifyAckAsync(d with { Title = title }, $"Belge güncellendi, yeniden onayınız gerekiyor: {title}", $"Document updated, please acknowledge again: {title}", ct);
        return Ok(new { id, version = no });
    }

    public record MetaInput(string? Category, string? Audience, Guid[]? DepartmentIds, bool? RequiresAck, bool? Archived);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> UpdateMeta(Guid id, [FromBody] MetaInput body, CancellationToken ct)
    {
        var d = (await Db.QueryAsync($"SELECT {Cols} FROM governance_library_documents d WHERE d.\"TenantSlug\" = $1 AND d.\"Id\" = $2", Map, ct, Tenant, id)).FirstOrDefault();
        if (d is null) return NotFound();
        var category = body.Category ?? d.Category;
        var audience = body.Audience ?? d.Audience;
        var depts = audience == Audience.Departments ? body.DepartmentIds?.Distinct().ToArray() ?? d.DepartmentIds : Array.Empty<Guid>();
        if (Validate(d.Title, category, audience, depts) is { } err) return BadRequest(new { message = err });
        await Db.ExecuteAsync("""
            UPDATE governance_library_documents SET "Category" = $3, "Audience" = $4, "DepartmentIds" = $5, "RequiresAck" = $6, "Archived" = $7, "UpdatedAt" = now()
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, ct, Tenant, id, category, audience, depts, body.RequiresAck ?? d.RequiresAck, body.Archived ?? d.Archived);
        return Ok(new { id });
    }

    public record AckInput(int? Version);

    /// <summary>"Okudum, kabul ediyorum" — güncel sürüm için. Okunan sürüm eskiyse 409 (yeni sürüm yayımlanmış).</summary>
    [HttpPost("{id:guid}/ack")]
    public async Task<IActionResult> Ack(Guid id, [FromBody] AckInput? body, CancellationToken ct)
    {
        var d = await FindVisibleAsync(id, ct);
        if (d is null || d.Archived) return NotFound(new { message = L("Belge bulunamadı.", "Document not found.") });
        if (d.CurrentVersionNo == 0) return Conflict(new { message = L("Belgenin yayımlanmış sürümü yok.", "No published version.") });
        if (body?.Version is { } v && v != d.CurrentVersionNo)
            return Conflict(new { message = L("Belgenin yeni bir sürümü yayımlandı; lütfen güncel sürümü okuyup yeniden onaylayın.", "A newer version was published; please read it and acknowledge again.") });
        await Acks.AddAsync(Db, Tenant, Acks.LibraryDocument, id, d.CurrentVersionNo, Me, await MyPersonAsync(ct), ct);
        return Ok(new { acknowledgedVersion = d.CurrentVersionNo });
    }

    /// <summary>Güncel sürümün onay istatistiği; onaylamayanlar yalnızca onay gerektiren belgede listelenir.</summary>
    [HttpGet("{id:guid}/stats")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct)
    {
        var d = (await Db.QueryAsync($"SELECT {Cols} FROM governance_library_documents d WHERE d.\"TenantSlug\" = $1 AND d.\"Id\" = $2", Map, ct, Tenant, id)).FirstOrDefault();
        if (d is null) return NotFound();
        var people = await People.ListAsync(Tenant, ct);
        var leaders = d.Audience == Audience.Managers ? await Audience.LeadersAsync(Db, Tenant, ct) : new HashSet<Guid>();
        var acks = (await Acks.ListAsync(Db, Tenant, Acks.LibraryDocument, id, ct)).Where(a => a.Version == d.CurrentVersionNo);
        return Ok(new { version = d.CurrentVersionNo, stats = Acks.Stats(Audience.Members(d.Audience, d.DepartmentIds, people, leaders), acks, listMissing: d.RequiresAck) });
    }

    [HttpGet("meta")]
    public IActionResult Meta() => Ok(new
    {
        categories = Categories.Select(c => new { value = c.Key, label = c.Value }),
        audiences = Audience.DocumentAudiences,
    });
}
