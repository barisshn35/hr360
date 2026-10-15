using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EngagementService.Data;
using EngagementService.Infrastructure;
using EngagementService.Models;

namespace EngagementService.Controllers;

/* ======================================================================
 * Takdir duvarı (kudos): çalışanlar birbirine rozetli teşekkür bırakır,
 * herkes görür ve beğenebilir. Alıcıya uygulama içi bildirim gider.
 * ==================================================================== */
[Route("api/kudos")]
[Authorize]
public class KudosController : AppController
{
    public static readonly Dictionary<string, string> Badges = new()
    {
        ["teamwork"] = "Takım oyuncusu",
        ["customer"] = "Müşteri kahramanı",
        ["innovation"] = "Yenilikçi fikir",
        ["mentor"] = "Yol gösterici",
        ["extra-mile"] = "Fazlasını yaptı",
        ["thanks"] = "Teşekkürler",
    };

    private readonly EngagementDbContext _db;
    private readonly Notifier _notify;
    public KudosController(EngagementDbContext db, Notifier notify) { _db = db; _notify = notify; }

    [HttpGet("badges")]
    public IActionResult GetBadges() => Ok(Badges.Select(b => new { id = b.Key, label = b.Value }));

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 50, [FromQuery] Guid? employeeId = null, CancellationToken ct = default)
    {
        var me = Me.UserId;
        var q = _db.Kudos.AsNoTracking().AsQueryable();
        if (employeeId is not null) q = q.Where(k => k.ToEmployeeId == employeeId);
        var items = await q.OrderByDescending(k => k.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        return Ok(items.Select(k => new
        {
            k.Id, k.FromName, k.FromEmployeeId, k.ToEmployeeId, k.ToName, k.Badge,
            badgeLabel = Badges.GetValueOrDefault(k.Badge, k.Badge), k.Message, k.CreatedAt,
            likeCount = k.LikedBy.Count, likedByMe = k.LikedBy.Contains(me), mine = k.FromUserId == me,
        }));
    }

    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        var rows = await _db.Kudos.AsNoTracking().Where(k => k.CreatedAt >= since).ToListAsync(ct);
        var top = rows.GroupBy(k => new { k.ToEmployeeId, k.ToName })
            .Select(g => new
            {
                employeeId = g.Key.ToEmployeeId, name = g.Key.ToName, count = g.Count(),
                topBadge = g.GroupBy(x => x.Badge).OrderByDescending(x => x.Count()).First().Key,
            })
            .OrderByDescending(x => x.count).Take(10);
        var badges = rows.GroupBy(k => k.Badge).Select(g => new { badge = g.Key, label = Badges.GetValueOrDefault(g.Key, g.Key), count = g.Count() });
        return Ok(new { total = rows.Count, givers = rows.Select(r => r.FromUserId).Distinct().Count(), top, badges });
    }

    public record CreateKudos(Guid ToEmployeeId, string Badge, string Message);

    [HttpPost]
    public async Task<IActionResult> Create(CreateKudos body, CancellationToken ct)
    {
        var from = await MyPersonAsync(ct);
        var (error, k) = await CreateCoreAsync(_db, _notify, People, Tenant, new Actor(Me.UserId, from?.Id, from?.Name ?? Me.Name), body, ct);
        return error?.ToResult() ?? Ok(new { k!.Id });
    }

    /// <summary>Takdir kuralları (saf): mesaj 1–500 karakter, rozet tanımlı olmalı.</summary>
    public static RuleError? Validate(string? message, string? badge)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
            return RuleError.Bad("Mesaj 1–500 karakter olmalı.", "message_length");
        if (badge is null || !Badges.ContainsKey(badge)) return RuleError.Bad("Geçersiz rozet.", "badge");
        return null;
    }

    /// <summary>Web ucu ve sohbet botu (/api/internal/chat/kudos) için ortak takdir oluşturma.</summary>
    internal static async Task<(RuleError? Error, Kudos? Kudos)> CreateCoreAsync(EngagementDbContext db, Notifier notify, PeopleDirectory people,
        string tenant, Actor from, CreateKudos body, CancellationToken ct)
    {
        if (Validate(body.Message, body.Badge) is { } invalid) return (invalid, null);
        var to = await people.FindAsync(tenant, body.ToEmployeeId, ct);
        if (to is null) return (RuleError.NotFound("Çalışan bulunamadı."), null);
        if (from.EmployeeId == to.Id) return (RuleError.Bad("Kendinize takdir gönderemezsiniz.", "self"), null);

        var k = new Kudos
        {
            FromUserId = from.UserId, FromEmployeeId = from.EmployeeId, FromName = from.Name,
            ToEmployeeId = to.Id, ToName = to.Name, Badge = body.Badge, Message = body.Message.Trim(),
        };
        db.Kudos.Add(k);
        await db.SaveChangesAsync(ct);
        await notify.InAppAsync(tenant, to.Id, $"{k.FromName} size takdir gönderdi: {Badges[k.Badge]}",
            k.Message, "engagement.kudos", ct);
        return (null, k);
    }

    [HttpPost("{id:guid}/like")]
    public async Task<IActionResult> Like(Guid id, CancellationToken ct)
    {
        var k = await _db.Kudos.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (k is null) return NotFound();
        var list = new List<string>(k.LikedBy);
        if (!list.Remove(Me.UserId)) list.Add(Me.UserId);
        k.LikedBy = list;
        await _db.SaveChangesAsync(ct);
        return Ok(new { likeCount = list.Count, likedByMe = list.Contains(Me.UserId) });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var k = await _db.Kudos.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (k is null) return NotFound();
        if (k.FromUserId != Me.UserId && !Me.IsHr) return Forbid();
        _db.Kudos.Remove(k);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/* ======================================================================
 * Kutlamalar: doğum günü (profilde paylaşmayı seçenler), iş yıl dönümü
 * (işe giriş tarihi) ve yeni katılanlar.
 * ==================================================================== */
[Route("api/celebrations")]
[Authorize]
public class CelebrationsController : AppController
{
    private readonly EngagementDbContext _db;
    public CelebrationsController(EngagementDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Upcoming([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 366);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)); // Türkiye saati
        var people = await People.ListAsync(Tenant, ct);
        var profiles = await _db.Profiles.AsNoTracking().Where(p => p.BirthDate != null && p.ShowBirthday).ToListAsync(ct);
        var byId = people.ToDictionary(p => p.Id);

        static DateOnly NextOccurrence(DateOnly date, DateOnly today)
        {
            var day = Math.Min(date.Day, DateTime.DaysInMonth(today.Year, date.Month));
            var next = new DateOnly(today.Year, date.Month, day);
            if (next < today)
            {
                day = Math.Min(date.Day, DateTime.DaysInMonth(today.Year + 1, date.Month));
                next = new DateOnly(today.Year + 1, date.Month, day);
            }
            return next;
        }

        var items = new List<Celebration>();
        foreach (var pr in profiles)
        {
            if (!byId.TryGetValue(pr.EmployeeId, out var p)) continue;
            var next = NextOccurrence(pr.BirthDate!.Value, today);
            var inDays = next.DayNumber - today.DayNumber;
            if (inDays <= days)
                items.Add(new Celebration("birthday", p.Id, p.Name, p.Department, next, inDays, null));
        }
        foreach (var p in people)
        {
            var next = NextOccurrence(p.HireDate, today);
            var years = next.Year - p.HireDate.Year;
            var inDays = next.DayNumber - today.DayNumber;
            if (years >= 1 && inDays <= days)
                items.Add(new Celebration("anniversary", p.Id, p.Name, p.Department, next, inDays, years));
            var since = today.DayNumber - p.HireDate.DayNumber;
            if (since is >= 0 and <= 30)
                items.Add(new Celebration("newcomer", p.Id, p.Name, p.Department, p.HireDate, -since, null));
        }
        return Ok(items.OrderBy(i => i.Kind == "newcomer" ? 1 : 0).ThenBy(i => Math.Abs(i.InDays)));
    }

    public record Celebration(string Kind, Guid EmployeeId, string Name, string? Department, DateOnly Date, int InDays, int? Years);
}

/* ======================================================================
 * Çalışan self-servis profili + yetenek dizini ("kim ne biliyor?").
 * IBAN ve TCKN gibi hassas alanlar yanıtta maskelenir; açık hâli yalnızca
 * sahibine veya İK'ya, ayrı bir uçtan ve denetim kaydı bırakılarak verilir.
 * ==================================================================== */
[Route("api/profile")]
[Authorize]
public class ProfileController : AppController
{
    private readonly EngagementDbContext _db;
    public ProfileController(EngagementDbContext db) => _db = db;

    public static string? Mask(string? value, int visible = 4)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var v = value.Replace(" ", "");
        return v.Length <= visible ? new string('•', v.Length) : new string('•', v.Length - visible) + v[^visible..];
    }

    private static object Shape(Person p, EmployeeProfile? pr, bool reveal) => new
    {
        employeeId = p.Id, name = p.Name, email = p.Email, position = p.Position, department = p.Department,
        hireDate = p.HireDate, status = p.Status,
        birthDate = pr?.BirthDate, showBirthday = pr?.ShowBirthday ?? true, bio = pr?.Bio, pronouns = pr?.Pronouns,
        skills = pr?.Skills ?? new(), interests = pr?.Interests ?? new(), address = pr?.Address,
        emergencyContactName = pr?.EmergencyContactName, emergencyContactPhone = pr?.EmergencyContactPhone,
        linkedInUrl = pr?.LinkedInUrl,
        iban = reveal ? pr?.Iban : Mask(pr?.Iban), nationalId = reveal ? pr?.NationalId : Mask(pr?.NationalId, 2),
        hasIban = !string.IsNullOrEmpty(pr?.Iban), hasNationalId = !string.IsNullOrEmpty(pr?.NationalId),
        updatedAt = pr?.UpdatedAt,
    };

    [HttpGet("me")]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        var p = await MyPersonAsync(ct);
        if (p is null) return NotFound(new { message = "Hesabınız bir çalışan kaydına bağlı değil." });
        var pr = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == p.Id, ct);
        return Ok(Shape(p, pr, false));
    }

    public record UpdateProfile(
        DateOnly? BirthDate, bool? ShowBirthday, string? Bio, string? Pronouns, List<string>? Skills,
        List<string>? Interests, string? Address, string? EmergencyContactName, string? EmergencyContactPhone,
        string? Iban, string? NationalId, string? LinkedInUrl);

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMine(UpdateProfile body, CancellationToken ct)
    {
        var p = await MyPersonAsync(ct);
        if (p is null) return NotFound(new { message = "Hesabınız bir çalışan kaydına bağlı değil." });
        if (body.Iban is { Length: > 0 } iban && !IsValidIban(iban))
            return BadRequest(new { message = "IBAN geçersiz (TR ile başlayan 26 karakter, kontrol basamağı tutmalı)." });
        if (body.NationalId is { Length: > 0 } tckn && !IsValidTckn(tckn))
            return BadRequest(new { message = "T.C. kimlik numarası geçersiz." });

        var pr = await _db.Profiles.FirstOrDefaultAsync(x => x.EmployeeId == p.Id, ct);
        if (pr is null) { pr = new EmployeeProfile { EmployeeId = p.Id }; _db.Profiles.Add(pr); }
        pr.BirthDate = body.BirthDate ?? pr.BirthDate;
        pr.ShowBirthday = body.ShowBirthday ?? pr.ShowBirthday;
        pr.Bio = body.Bio?.Trim() ?? pr.Bio;
        pr.Pronouns = body.Pronouns ?? pr.Pronouns;
        if (body.Skills is not null) pr.Skills = Normalize(body.Skills);
        if (body.Interests is not null) pr.Interests = Normalize(body.Interests);
        pr.Address = body.Address ?? pr.Address;
        pr.EmergencyContactName = body.EmergencyContactName ?? pr.EmergencyContactName;
        pr.EmergencyContactPhone = body.EmergencyContactPhone ?? pr.EmergencyContactPhone;
        // Maskeli değer geri gönderildiyse (•• içerir) dokunma.
        if (body.Iban is not null && !body.Iban.Contains('•')) pr.Iban = body.Iban.Trim() == "" ? null : body.Iban.Replace(" ", "").ToUpperInvariant();
        if (body.NationalId is not null && !body.NationalId.Contains('•')) pr.NationalId = body.NationalId.Trim() == "" ? null : body.NationalId.Trim();
        pr.LinkedInUrl = body.LinkedInUrl ?? pr.LinkedInUrl;
        pr.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Shape(p, pr, false));
    }

    /// <summary>Maskeli alanın açık hâli. Sahibi veya İK; her açılış denetim kaydına yazılır.</summary>
    [HttpGet("{employeeId:guid}/reveal")]
    public async Task<IActionResult> Reveal(Guid employeeId, [FromQuery] string field, [FromQuery] string? reason, CancellationToken ct)
    {
        if (field is not ("iban" or "nationalId")) return BadRequest(new { message = "Geçersiz alan." });
        var mine = await MyPersonAsync(ct);
        var own = mine?.Id == employeeId;
        if (!own && !Me.IsHr) return Forbid();
        // G20: kiracı alanı "yalnızca kendisi" yaptıysa İK da açamaz.
        if (!own && (await FieldPolicies.ForTenantAsync(Db, Tenant, ct))[field] == "self")
            return StatusCode(403, new { message = "Bu alan şirket politikasıyla yalnızca çalışanın kendisine açık.", code = "field_policy" });
        // KVKK m.12: başkasının TCKN/IBAN'ını açan kişi gerekçe yazar; gerekçe erişim kaydına girer.
        if (!own && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5))
            return BadRequest(new { message = "Başka bir çalışanın bu bilgisini görmek için gerekçe yazın (en az 5 karakter).", code = "reason_required" });
        // Güvenlik dalgası 2B: toplu görüntüleme uyarısı sonrası (kiracı ayarı açıksa) geçici engel.
        if (!own && await RevealBlockedAsync(ct))
            return StatusCode(423, new { message = "Kısa sürede çok sayıda çalışan kaydı açıldığı için hassas alan açma geçici olarak engellendi. Şirket yöneticinize başvurun.", code = "mass_view_block" });
        var pr = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == employeeId, ct);
        string? value = field == "iban" ? pr?.Iban : pr?.NationalId;
        await LogSensitiveAsync(employeeId, "Revealed", field, own ? null : reason!.Trim(), ct);
        return Ok(new { field, value });
    }

    /// <summary>governance-service'in toplu görüntüleme dedektörünün yazdığı geçici engel (governance_security_alerts) okunur.</summary>
    private async Task<bool> RevealBlockedAsync(CancellationToken ct)
    {
        try
        {
            return await Db.ScalarAsync("""
                SELECT 1 FROM governance_security_alerts WHERE "TenantSlug" = $1 AND "UserId" = $2 AND "BlockedUntil" > now() LIMIT 1
                """, ct, Tenant, Me.UserId) is not null;
        }
        catch (Npgsql.PostgresException)
        {
            return false; // tablo yoksa (göç uygulanmadı) engel yok
        }
    }

    /// <summary>Hassas veri erişimini denetim kaydına yazar (KVKK › Erişim kayıtları ekranı okur).</summary>
    private Task LogSensitiveAsync(Guid employeeId, string action, string field, string? reason, CancellationToken ct) =>
        Db.ExecuteAsync(
            """
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","CorrelationId","IpAddress","OccurredAt")
            VALUES ($1,'engagement-service','EmployeeProfile',$2,$3,$4::jsonb,$5,$6,$7,$8,now())
            """, ct, Tenant, employeeId.ToString(), action,
            System.Text.Json.JsonSerializer.Serialize(reason is null ? new Dictionary<string, string> { ["field"] = field } : new() { ["field"] = field, ["reason"] = reason[..Math.Min(500, reason.Length)] }),
            Me.UserId, Me.Name,
            HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier,
            HttpContext.Request.Headers["X-Real-IP"].FirstOrDefault());

    [HttpGet("{employeeId:guid}")]
    public async Task<IActionResult> Get(Guid employeeId, CancellationToken ct)
    {
        var p = await People.FindAsync(Tenant, employeeId, ct);
        if (p is null) return NotFound();
        var pr = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == employeeId, ct);
        var mine = await MyPersonAsync(ct);
        // G20: görüntüleyenin düzeyi ve kiracının alan politikası.
        var viewer = mine?.Id == employeeId ? "self" : Me.IsHr ? "hr"
            : mine is not null && p.DepartmentHeadId == mine.Id ? "manager" : "other";
        var policy = await FieldPolicies.ForTenantAsync(Db, Tenant, ct);
        bool See(string f) => FieldPolicies.CanSee(policy[f], viewer);
        var hidden = policy.Keys.Where(f => !See(f)).ToList();
        if (viewer is "self" or "hr")
        {
            // İK başkasının özel profil alanlarını (adres, doğum tarihi, acil durum kişisi) görüntüledi.
            if (viewer == "hr" && pr is not null && (pr.Address ?? pr.EmergencyContactName ?? pr.EmergencyContactPhone ?? (object?)pr.BirthDate) is not null)
                await LogSensitiveAsync(employeeId, "SensitiveViewed", "profile", null, ct);
            var full = Shape(p, pr, false);
            if (hidden.Count == 0) return Ok(full);
            // Kiracı bir alanı "yalnızca kendisi" yaptıysa İK da göremez.
            var d = System.Text.Json.JsonSerializer.SerializeToNode(full)!.AsObject();
            foreach (var f in hidden)
                foreach (var k in f == "emergencyContact" ? new[] { "emergencyContactName", "emergencyContactPhone" } : new[] { f })
                    d[k] = null;
            d["hiddenFields"] = System.Text.Json.JsonSerializer.SerializeToNode(hidden);
            return Ok(d);
        }
        if (viewer == "manager" && pr is not null && (See("address") && pr.Address is not null || See("emergencyContact") && pr.EmergencyContactName is not null || See("birthDate") && pr.BirthDate is not null))
            await LogSensitiveAsync(employeeId, "SensitiveViewed", "profile", null, ct);
        // Diğer çalışanlar/yönetici yalnızca politikanın izin verdiği alanları görür.
        return Ok(new
        {
            employeeId = p.Id, name = p.Name, email = p.Email, position = p.Position, department = p.Department,
            bio = See("bio") ? pr?.Bio : null, pronouns = See("pronouns") ? pr?.Pronouns : null,
            skills = See("skills") ? pr?.Skills ?? new() : new(), interests = See("interests") ? pr?.Interests ?? new() : new(),
            linkedInUrl = See("linkedInUrl") ? pr?.LinkedInUrl : null,
            birthDate = See("birthDate") ? pr?.BirthDate : null, address = See("address") ? pr?.Address : null,
            emergencyContactName = See("emergencyContact") ? pr?.EmergencyContactName : null,
            emergencyContactPhone = See("emergencyContact") ? pr?.EmergencyContactPhone : null,
            hiddenFields = hidden,
        });
    }

    /// <summary>Yetenek dizini: "Kim Kubernetes biliyor?" — profilde beceri girenler.</summary>
    [HttpGet("directory")]
    public async Task<IActionResult> Directory([FromQuery] string? q, CancellationToken ct)
    {
        var people = await People.ListAsync(Tenant, ct);
        var profiles = (await _db.Profiles.AsNoTracking().ToListAsync(ct)).ToDictionary(p => p.EmployeeId);
        var policy = await FieldPolicies.ForTenantAsync(Db, Tenant, ct);
        var dirViewer = Me.IsHr ? "hr" : "other";
        bool DirSee(string f) => FieldPolicies.CanSee(policy[f], dirViewer);
        // Çalışan listesindeki aramayla aynı katlama (Paging.Fold / SQL hr360_fold): "AYSE" = "ayşe".
        static string Fold(string? value)
        {
            var s = (value ?? "").Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant();
            return string.Concat(s.Select(ch => ch switch
            {
                'ı' => 'i', 'ş' => 's', 'ğ' => 'g', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', 'î' => 'i', 'û' => 'u', _ => ch,
            }));
        }
        var term = Fold(q?.Trim());
        var rows = people.Select(p =>
        {
            profiles.TryGetValue(p.Id, out var pr);
            return new
            {
                employeeId = p.Id, name = p.Name, position = p.Position, department = p.Department, email = p.Email,
                skills = DirSee("skills") ? pr?.Skills ?? new() : new(), interests = DirSee("interests") ? pr?.Interests ?? new() : new(),
                bio = DirSee("bio") ? pr?.Bio : null,
            };
        });
        if (!string.IsNullOrEmpty(term))
            rows = rows.Where(r => Fold(r.name).Contains(term)
                || Fold(r.position).Contains(term)
                || Fold(r.department).Contains(term)
                || r.skills.Any(s => Fold(s).Contains(term))
                || r.interests.Any(s => Fold(s).Contains(term)));
        var list = rows.ToList();
        var topSkills = list.SelectMany(r => r.skills).GroupBy(s => s.ToLowerInvariant())
            .Select(g => new { skill = g.First(), count = g.Count() }).OrderByDescending(x => x.count).Take(20);
        return Ok(new { people = list, topSkills });
    }

    private static List<string> Normalize(IEnumerable<string> items) =>
        items.Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();

    public static bool IsValidIban(string raw)
    {
        var iban = raw.Replace(" ", "").ToUpperInvariant();
        if (iban.Length != 26 || !iban.StartsWith("TR")) return false;
        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var part = char.IsLetter(ch) ? (ch - 'A' + 10).ToString() : ch.ToString();
            foreach (var d in part) remainder = (remainder * 10 + (d - '0')) % 97;
        }
        return remainder == 1;
    }

    public static bool IsValidTckn(string t)
    {
        if (t.Length != 11 || !t.All(char.IsDigit) || t[0] == '0') return false;
        var d = t.Select(c => c - '0').ToArray();
        var d10 = ((d[0] + d[2] + d[4] + d[6] + d[8]) * 7 - (d[1] + d[3] + d[5] + d[7])) % 10;
        if (d10 < 0) d10 += 10;
        var d11 = d.Take(10).Sum() % 10;
        return d[9] == d10 && d[10] == d11;
    }
}
