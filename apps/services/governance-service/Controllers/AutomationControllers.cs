using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Models;

namespace GovernanceService.Controllers;

/// <summary>Kural/webhook ekranlarında seçilebilen olay tipleri ve yüklerindeki alanlar.</summary>
public static class EventCatalog
{
    public static readonly (string Type, string Label, string[] Fields)[] Types =
    {
        ("employee.hired", "Çalışan işe alındı", new[] { "EmployeeId", "FirstName", "LastName", "Email", "HireDate" }),
        ("employee.assigned", "Görevlendirme yapıldı", new[] { "EmployeeId", "DepartmentId", "PositionTitle", "EffectiveFrom", "FirstName", "LastName" }),
        ("employee.status-changed", "Çalışan durumu değişti", new[] { "EmployeeId", "FirstName", "LastName", "OldStatus", "NewStatus", "EffectiveDate" }),
        ("workflow.submitted", "Onaya gönderildi", new[] { "WorkflowRequestId", "WorkflowType", "RequesterEmployeeId", "RequesterName", "Subject", "ApproverEmployeeId" }),
        ("workflow.approved", "Talep onaylandı", new[] { "WorkflowRequestId", "WorkflowType", "RequesterEmployeeId", "Subject" }),
        ("workflow.rejected", "Talep reddedildi", new[] { "WorkflowRequestId", "WorkflowType", "RequesterEmployeeId", "Subject" }),
        ("leave.approved", "İzin onaylandı", new[] { "LeaveRequestId", "EmployeeId", "Type", "StartDate", "EndDate", "Days" }),
        ("document.signed", "Belge imzalandı (basit e-imza)", new[] { "DocumentId", "DocumentType", "TemplateName", "EmployeeId", "SignedAt", "Method", "DocumentSha256" }),
        ("*", "Tüm olaylar", Array.Empty<string>()),
    };
}

/* ======================================================================
 * Kural motoru: "olay X olduğunda, koşullar sağlanıyorsa, şunları yap".
 * Örn. "5 günden uzun izin onaylandığında İK kanalına Slack mesajı at".
 * Değerlendirme governance-service'in Kafka tüketicisinde çalışır.
 * ==================================================================== */
[Route("api/rules")]
[Authorize(Policy = "RequireHrAdmin")]
public class RulesController : AppController
{
    private readonly GovernanceDbContext _db;
    public RulesController(GovernanceDbContext db) => _db = db;

    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(new
    {
        events = EventCatalog.Types.Select(t => new { type = t.Type, label = t.Label, fields = t.Fields }),
        operators = new[] { new { op = "eq", label = "eşittir" }, new { op = "neq", label = "eşit değil" }, new { op = "gt", label = "büyüktür" },
            new { op = "gte", label = "büyük/eşit" }, new { op = "lt", label = "küçüktür" }, new { op = "lte", label = "küçük/eşit" }, new { op = "contains", label = "içerir" } },
        actions = new[] { new { type = "notify", label = "Uygulama içi bildirim" }, new { type = "slack", label = "Slack mesajı" },
            new { type = "teams", label = "Teams mesajı" }, new { type = "webhook", label = "Webhook (HTTP POST)" } },
    });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _db.Rules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct));

    public record RuleInput(string Name, string? Description, string Trigger, List<RuleCondition>? Conditions, List<RuleAction>? Actions, bool IsEnabled);

    private IActionResult? Validate(RuleInput b)
    {
        if (string.IsNullOrWhiteSpace(b.Name)) return BadRequest(new { message = "Kural adı zorunlu." });
        if (EventCatalog.Types.All(t => t.Type != b.Trigger)) return BadRequest(new { message = "Geçersiz tetikleyici." });
        if (b.Actions is null || b.Actions.Count == 0) return BadRequest(new { message = "En az bir eylem gerekli." });
        if (b.Actions.Any(a => a.Type is not ("notify" or "slack" or "teams" or "webhook"))) return BadRequest(new { message = "Geçersiz eylem." });
        if (b.Actions.Any(a => a.Type == "webhook" && !Uri.TryCreate(a.Target, UriKind.Absolute, out _))) return BadRequest(new { message = "Webhook adresi geçersiz." });
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> Create(RuleInput body, CancellationToken ct)
    {
        if (Validate(body) is { } err) return err;
        var r = new Rule { Name = body.Name.Trim(), Description = body.Description, Trigger = body.Trigger, Conditions = body.Conditions ?? new(), Actions = body.Actions!, IsEnabled = body.IsEnabled };
        _db.Rules.Add(r);
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, RuleInput body, CancellationToken ct)
    {
        if (Validate(body) is { } err) return err;
        var r = await _db.Rules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        r.Name = body.Name.Trim(); r.Description = body.Description; r.Trigger = body.Trigger;
        r.Conditions = body.Conditions ?? new(); r.Actions = body.Actions!; r.IsEnabled = body.IsEnabled;
        _db.Entry(r).Property(x => x.Conditions).IsModified = true;
        _db.Entry(r).Property(x => x.Actions).IsModified = true;
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await _db.Rules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        _db.Rules.Remove(r);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record TestInput(List<RuleCondition> Conditions, List<RuleAction>? Actions, JsonElement Payload);

    /// <summary>Kuralı örnek bir olay yüküne karşı kuru çalıştırır (hiçbir şey göndermez).</summary>
    [HttpPost("test")]
    public IActionResult Test(TestInput body)
    {
        var (matched, why) = Dispatcher.Evaluate(body.Conditions, body.Payload);
        var preview = (body.Actions ?? new()).Select(a => new { a.Type, a.Target, message = Dispatcher.Render(string.IsNullOrWhiteSpace(a.Message) ? "{{ozet}}" : a.Message, body.Payload, "örnek olay") });
        return Ok(new { matched, reason = why, actions = matched ? preview : null });
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs([FromQuery] Guid? ruleId, CancellationToken ct)
    {
        var q = _db.RuleRuns.AsNoTracking();
        if (ruleId is not null) q = q.Where(r => r.RuleId == ruleId);
        return Ok(await q.OrderByDescending(r => r.OccurredAt).Take(100).ToListAsync(ct));
    }

    [HttpPost("samples")]
    public async Task<IActionResult> Samples(CancellationToken ct)
    {
        if (await _db.Rules.AnyAsync(ct)) return Conflict(new { message = "Zaten kural tanımlı." });
        _db.Rules.Add(new Rule
        {
            Name = "Uzun izin bildirimi", Description = "5 günden uzun izin onaylandığında çalışana iyi tatiller mesajı.", Trigger = "leave.approved",
            Conditions = new() { new() { Field = "Days", Op = "gt", Value = "5" } },
            Actions = new() { new() { Type = "notify", Target = "employee", Message = "{{Days}} günlük izniniz onaylandı. {{StartDate}} — iyi dinlenmeler!" } },
        });
        _db.Rules.Add(new Rule
        {
            Name = "Yeni çalışan → İK kanalı", Description = "İşe alımda Slack/Teams kanallarına duyuru.", Trigger = "employee.hired",
            Actions = new() { new() { Type = "slack", Message = "Aramıza yeni katılan {{FirstName}} {{LastName}}'a hoş geldin diyelim! 🎉" } }, IsEnabled = false,
        });
        _db.Rules.Add(new Rule
        {
            Name = "Ayrılışta onay sahibine bilgi", Description = "Çalışan ayrıldığında kendisine veda notu.", Trigger = "employee.status-changed",
            Conditions = new() { new() { Field = "NewStatus", Op = "eq", Value = "Terminated" } },
            Actions = new() { new() { Type = "notify", Target = "employee", Message = "{{FirstName}}, birlikte çalıştığımız için teşekkürler." } }, IsEnabled = false,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { added = 3 });
    }
}

/* ======================================================================
 * Giden webhook'lar (HMAC-SHA256 imzalı) + test alıcısı. Test alıcısı,
 * dış ağa çıkmadan imzalı teslimatı uçtan uca görmeyi sağlar.
 * ==================================================================== */
[Route("api/webhooks")]
[Authorize(Policy = "RequireHrAdmin")]
public class WebhooksController : AppController
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<object>> Inbox = new();
    private readonly GovernanceDbContext _db;
    private readonly Dispatcher _dispatcher;
    public WebhooksController(GovernanceDbContext db, Dispatcher dispatcher) { _db = db; _dispatcher = dispatcher; }

    public static string NewSecret() => "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>K4/G29: Zapier gibi yurt dışı hedefe aktarım dayanağı olmadan webhook açılamaz.</summary>
    private async Task<IActionResult?> TransferLockAsync(string url, CancellationToken ct) =>
        TransferGuard.HookProvider(url) is { } p && await TransferGuard.MissingAsync(_db, Tenant, p, ct) is { } msg
            ? Conflict(new { message = En ? TransferGuard.MessageEn(p) : msg, code = "transfer_basis_required", provider = p })
            : null;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _db.Webhooks.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct));

    public record HookInput(string Name, string Url, List<string> Events, bool IsEnabled);

    [HttpPost]
    public async Task<IActionResult> Create(HookInput body, CancellationToken ct)
    {
        if (WebhookTargetGuard.Validate(body.Url) is { } invalid) return BadRequest(new { message = invalid, code = "invalid_target" });
        if (body.Events.Count == 0) return BadRequest(new { message = "En az bir olay seçin." });
        if (await TransferLockAsync(body.Url, ct) is { } locked) return locked;
        var w = new Webhook { Name = body.Name.Trim(), Url = body.Url, Events = body.Events, IsEnabled = body.IsEnabled, Secret = NewSecret() };
        _db.Webhooks.Add(w);
        await _db.SaveChangesAsync(ct);
        return Ok(w);
    }

    /// <summary>Servisin kendi test alıcısına bağlı bir webhook oluşturur.</summary>
    [HttpPost("test-receiver")]
    public async Task<IActionResult> CreateTestReceiver(CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var w = new Webhook
        {
            Name = "Test alıcısı", Url = $"http://governance-service:8080/api/webhooks/inbox/{token}", Events = new() { "*" }, Secret = NewSecret(),
        };
        _db.Webhooks.Add(w);
        await _db.SaveChangesAsync(ct);
        return Ok(new { w.Id, token });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, HookInput body, CancellationToken ct)
    {
        var w = await _db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        if (WebhookTargetGuard.Validate(body.Url) is { } invalid) return BadRequest(new { message = invalid, code = "invalid_target" });
        if (await TransferLockAsync(body.Url, ct) is { } locked) return locked;
        w.Name = body.Name.Trim(); w.Url = body.Url; w.Events = body.Events; w.IsEnabled = body.IsEnabled;
        if (body.IsEnabled) w.FailureCount = 0;
        await _db.SaveChangesAsync(ct);
        return Ok(w);
    }

    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<IActionResult> Rotate(Guid id, CancellationToken ct)
    {
        var w = await _db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        w.Secret = NewSecret();
        await _db.SaveChangesAsync(ct);
        return Ok(new { w.Secret });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var w = await _db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        _db.Webhooks.Remove(w);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/ping")]
    public async Task<IActionResult> Ping(Guid id, CancellationToken ct)
    {
        var w = await _db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { TenantSlug = w.TenantSlug, message = "HR360 webhook testi", at = DateTime.UtcNow })).RootElement;
        await _dispatcher.DeliverWebhookAsync(_db, w, Guid.NewGuid(), "ping", payload, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { w.LastStatus, ok = w.LastStatus is >= 200 and < 300 });
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<IActionResult> Deliveries(Guid id, CancellationToken ct) =>
        Ok(await _db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == id).OrderByDescending(d => d.OccurredAt).Take(50).ToListAsync(ct));

    /* ------------------------------------------------------------ Dalga 12 (madde 93): teslimat hataları ekranı */

    /// <summary>
    /// Kiracının tüm webhook teslimatları: state = failed (varsayılan) | pending (sıradaki denemeler) |
    /// gave_up | all. Yük (payload) dönmez; yeniden gönderilebilirlik olay kaydının (30 gün) varlığına bağlıdır.
    /// </summary>
    [HttpGet("deliveries")]
    public async Task<IActionResult> AllDeliveries([FromQuery] string? state, [FromQuery] Guid? webhookId, [FromQuery] string? eventType,
        [FromQuery] int? take, CancellationToken ct)
    {
        var q = _db.WebhookDeliveries.AsNoTracking();
        q = (state ?? "failed") switch
        {
            "pending" => q.Where(d => d.RetryState == "pending" || d.RetryState == "retrying"),
            "gave_up" => q.Where(d => d.RetryState == "gave_up"),
            "all" => q,
            _ => q.Where(d => d.Error != null),
        };
        if (webhookId is { } wid) q = q.Where(d => d.WebhookId == wid);
        if (!string.IsNullOrWhiteSpace(eventType)) q = q.Where(d => d.EventType == eventType);
        var rows = await q.OrderByDescending(d => d.OccurredAt).Take(Math.Clamp(take ?? 100, 1, 200)).ToListAsync(ct);
        var hookIds = rows.Select(r => r.WebhookId).Distinct().ToList();
        var hooks = await _db.Webhooks.AsNoTracking().Where(w => hookIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new { w.Name, w.Url, w.IsEnabled }, ct);
        var eventIds = rows.Where(r => r.EventId != null).Select(r => r.EventId!.Value).Distinct().ToList();
        var alive = (await _db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id) && e.TenantSlug == Tenant).Select(e => e.Id).ToListAsync(ct)).ToHashSet();

        var since = DateTime.UtcNow.AddHours(-24);
        var last24 = await _db.WebhookDeliveries.AsNoTracking().Where(d => d.OccurredAt >= since)
            .GroupBy(_ => 1).Select(g => new { total = g.Count(), failed = g.Count(d => d.Error != null) }).FirstOrDefaultAsync(ct);
        var pending = await _db.WebhookDeliveries.AsNoTracking().CountAsync(d => d.RetryState == "pending" || d.RetryState == "retrying", ct);
        var gaveUp = await _db.WebhookDeliveries.AsNoTracking().CountAsync(d => d.RetryState == "gave_up" && d.OccurredAt >= since.AddDays(-6), ct);
        return Ok(new
        {
            items = rows.Select(d => new
            {
                d.Id, d.WebhookId, webhookName = hooks.TryGetValue(d.WebhookId, out var h) ? h.Name : null,
                webhookUrl = h?.Url, webhookEnabled = h?.IsEnabled ?? false,
                d.EventId, d.EventType, d.StatusCode, d.Error, d.DurationMs, d.OccurredAt, d.Attempt, d.NextRetryAt, d.RetryState, d.Manual, d.TriggeredByName,
                canResend = h is not null && (d.EventType == "ping" || (d.EventId is { } ev && alive.Contains(ev))) && d.Error != "transfer_basis_required",
            }),
            summary = new { total24h = last24?.total ?? 0, failed24h = last24?.failed ?? 0, pendingRetries = pending, gaveUp7d = gaveUp },
            retry = new { maxAttempts = WebhookRetry.MaxAttempts, scheduleSeconds = WebhookRetry.ScheduleSeconds() },
        });
    }

    /// <summary>Elle yeniden gönderim: aynı olay kimliğiyle (alıcı tekilleştirebilir) yeni deneme. Denetim kaydına yazılır.</summary>
    [HttpPost("deliveries/{deliveryId:guid}/resend")]
    public async Task<IActionResult> Resend(Guid deliveryId, CancellationToken ct)
    {
        var d = await _db.WebhookDeliveries.FirstOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (d is null) return NotFound();
        if (d.Error == "transfer_basis_required")
            return Conflict(new { message = L("Bu hedefe KVKK aktarım dayanağı olmadan gönderim yapılamaz.", "Transfer basis is required for this target."), code = "transfer_basis_required" });
        var (sent, err) = await WebhookRetry.ResendAsync(_db, Db, _dispatcher, d, true, Me.Name, ct);
        if (err == "hook_missing") return NotFound(new { message = L("Webhook silinmiş.", "Webhook was deleted."), code = err });
        if (err == "event_expired")
            return Conflict(new { message = L("Olay kaydı saklama süresi (30 gün) dolduğu için yeniden gönderilemez.", "The event record has expired (30 days) and cannot be resent."), code = err });
        await _db.SaveChangesAsync(ct);
        await ComplianceAudit.WriteAsync(Db, Tenant, "WebhookDelivery", deliveryId.ToString(), "Resent",
            new { webhookId = d.WebhookId, d.EventType, newDeliveryId = sent!.Id, sent.StatusCode, attempt = sent.Attempt }, Me.UserId, Me.Name, ct);
        return Ok(new { sent.Id, sent.StatusCode, sent.Error, sent.Attempt, sent.RetryState, sent.NextRetryAt, ok = sent.Error is null });
    }

    /// <summary>Sıradaki otomatik denemeyi durdurur (ör. alıcı bakımdayken).</summary>
    [HttpPost("deliveries/{deliveryId:guid}/cancel-retry")]
    public async Task<IActionResult> CancelRetry(Guid deliveryId, CancellationToken ct)
    {
        var d = await _db.WebhookDeliveries.FirstOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (d is null) return NotFound();
        if (d.RetryState != "pending") return Conflict(new { message = L("Sırada bekleyen deneme yok.", "No pending retry."), code = "not_pending" });
        d.RetryState = "gave_up";
        d.NextRetryAt = null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { d.Id, d.RetryState });
    }

    /// <summary>Test alıcısı: imzayı doğrular ve son 50 teslimatı bellekte tutar.</summary>
    [HttpPost("inbox/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Receive(string token, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        var sig = Request.Headers["X-HR360-Signature"].FirstOrDefault();
        var secret = await Db.ScalarAsync("SELECT \"Secret\" FROM governance_webhooks WHERE \"Url\" LIKE $1 LIMIT 1", ct, $"%/inbox/{token}") as string;
        bool? valid = secret is null || sig is null ? null
            : CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sig),
                Encoding.UTF8.GetBytes("sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant()));
        var q = Inbox.GetOrAdd(token, _ => new ConcurrentQueue<object>());
        q.Enqueue(new
        {
            receivedAt = DateTime.UtcNow, @event = Request.Headers["X-HR360-Event"].FirstOrDefault(), signatureValid = valid,
            body = body.Length > 4000 ? body[..4000] : body
        });
        while (q.Count > 50) q.TryDequeue(out _);
        return Ok(new { received = true });
    }

    [HttpGet("inbox/{token}")]
    public IActionResult ReadInbox(string token) =>
        Ok(Inbox.TryGetValue(token, out var q) ? q.Reverse().ToArray() : Array.Empty<object>());
}

/* ======================================================================
 * API anahtarları — açık API (api/public/v1) için. Anahtar yalnızca
 * oluşturulduğu an bir kez gösterilir; veritabanında SHA-256 özeti tutulur.
 * ==================================================================== */
[Route("api/api-keys")]
[Authorize(Policy = "RequireHrAdmin")]
public class ApiKeysController : AppController
{
    /// <summary>
    /// hooks:write — Zapier/n8n REST hook aboneliği (POST/DELETE /hooks). Dalga 12: "read-only"
    /// tüm okuma yetkilerini kapsar (bkz. <see cref="ApiScopes"/>).
    /// </summary>
    public static readonly string[] AllScopes = ApiScopes.All;
    private readonly GovernanceDbContext _db;
    public ApiKeysController(GovernanceDbContext db) => _db = db;

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static (string Prefix, string Key) NewKey()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');
        return (prefix, $"hr360_{prefix}_{secret}");
    }

    private static object View(ApiKey k, DateTime now, long last30 = 0, long denied30 = 0) => new
    {
        k.Id, k.Name, k.Prefix, k.Scopes, k.CreatedByName, k.CreatedAt, k.LastUsedAt, k.RevokedAt, k.ExpiresAt, k.RotatedFromId,
        k.UsageCount, k.LastUsedScope, last30, denied30,
        status = ApiScopes.Inactive(k.RevokedAt, k.ExpiresAt, now) ?? "active",
        active = ApiScopes.Inactive(k.RevokedAt, k.ExpiresAt, now) is null,
        expiresSoon = k.RevokedAt is null && k.ExpiresAt is { } e && e > now && e <= now.AddDays(14),
    };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var keys = await _db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        // Son 30 günün toplamları tek sorguda (anahtar başına istek / ret).
        var usage = (await Db.QueryAsync("""
            SELECT "KeyId", sum("Count")::bigint, sum("DeniedCount")::bigint FROM governance_api_key_usage
            WHERE "TenantSlug" = $1 AND "Day" >= current_date - 29 GROUP BY "KeyId"
            """, r => (Id: r.GetGuid(0), Count: r.GetInt64(1), Denied: r.GetInt64(2)), ct, Tenant)).ToDictionary(x => x.Id);
        return Ok(keys.Select(k => usage.TryGetValue(k.Id, out var u) ? View(k, now, u.Count, u.Denied) : View(k, now)));
    }

    [HttpGet("scopes")]
    public IActionResult Scopes() => Ok(AllScopes);

    public record KeyInput(string Name, List<string> Scopes, int? ExpiresInDays = null);

    [HttpPost]
    public async Task<IActionResult> Create(KeyInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { message = "Ad zorunlu." });
        var scopes = ApiScopes.Normalize(body.Scopes);
        if (scopes.Count == 0) return BadRequest(new { message = "En az bir yetki seçin." });
        var (prefix, key) = NewKey();
        var k = new ApiKey
        {
            Name = body.Name.Trim(), Prefix = prefix, KeyHash = Hash(key), Scopes = scopes, CreatedByName = Me.Name,
            ExpiresAt = ApiScopes.ExpiryFromDays(body.ExpiresInDays, DateTime.UtcNow),
        };
        _db.ApiKeys.Add(k);
        await _db.SaveChangesAsync(ct);
        return Ok(new { k.Id, k.Name, k.Prefix, k.Scopes, k.ExpiresAt, key });
    }

    public record RotateInput(int? GraceHours, int? ExpiresInDays);

    /// <summary>
    /// Döndürme: aynı ad ve yetkilerle yeni anahtar üretir (yalnızca bu yanıtta görünür). Eski anahtar
    /// geçiş süresi (0-72 sa, varsayılan 24) sonunda kendiliğinden geçersizleşir; 0 ise hemen iptal edilir.
    /// Eski anahtarla açılmış REST hook abonelikleri yeni anahtara taşınır.
    /// </summary>
    [HttpPost("{id:guid}/rotate")]
    public async Task<IActionResult> Rotate(Guid id, [FromBody] RotateInput? body, CancellationToken ct)
    {
        var old = await _db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (old is null) return NotFound();
        var now = DateTime.UtcNow;
        if (ApiScopes.Inactive(old.RevokedAt, old.ExpiresAt, now) is { } st)
            return Conflict(new { message = L("İptal edilmiş ya da süresi dolmuş anahtar döndürülemez.", "A revoked or expired key cannot be rotated."), code = "key_" + st });
        var grace = Math.Clamp(body?.GraceHours ?? 24, 0, 72);
        var (prefix, key) = NewKey();
        // Yeni anahtarın süresi: istenen gün ya da eski anahtarın toplam ömrü kadar (süresizse süresiz).
        var lifetime = old.ExpiresAt is { } e ? e - old.CreatedAt : (TimeSpan?)null;
        var k = new ApiKey
        {
            Name = old.Name, Prefix = prefix, KeyHash = Hash(key), Scopes = ApiScopes.Normalize(old.Scopes), CreatedByName = Me.Name, RotatedFromId = old.Id,
            ExpiresAt = body?.ExpiresInDays is not null ? ApiScopes.ExpiryFromDays(body.ExpiresInDays, now) : lifetime is { } l ? now.Add(l) : null,
        };
        _db.ApiKeys.Add(k);
        if (grace == 0) old.RevokedAt = now;
        else old.ExpiresAt = old.ExpiresAt is { } oe && oe < now.AddHours(grace) ? oe : now.AddHours(grace);
        await _db.SaveChangesAsync(ct);
        await Db.ExecuteAsync("UPDATE governance_webhooks SET \"ApiKeyId\" = $1 WHERE \"TenantSlug\" = $2 AND \"ApiKeyId\" = $3", ct, k.Id, Tenant, old.Id);
        return Ok(new { k.Id, k.Name, k.Prefix, k.Scopes, k.ExpiresAt, key, oldKeyValidUntil = grace == 0 ? now : old.ExpiresAt });
    }

    /// <summary>Kullanım istatistiği: gün ve yetki bazında istek / ret sayıları (en çok 90 gün).</summary>
    [HttpGet("{id:guid}/usage")]
    public async Task<IActionResult> Usage(Guid id, [FromQuery] int? days, CancellationToken ct)
    {
        var k = await _db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (k is null) return NotFound();
        var n = Math.Clamp(days ?? 30, 1, 90);
        var rows = await Db.QueryAsync("""
            SELECT "Day", "Scope", "Count", "DeniedCount" FROM governance_api_key_usage
            WHERE "TenantSlug" = $1 AND "KeyId" = $2 AND "Day" >= current_date - $3::int ORDER BY "Day", "Scope"
            """, r => new { day = r.GetFieldValue<DateOnly>(0), scope = r.GetString(1), count = r.GetInt32(2), denied = r.GetInt32(3) }, ct, Tenant, id, n - 1);
        return Ok(new
        {
            id = k.Id, days = n, total = rows.Sum(r => (long)r.count), denied = rows.Sum(r => (long)r.denied),
            byDay = rows.GroupBy(r => r.day).Select(g => new { day = g.Key, count = g.Sum(x => x.count), denied = g.Sum(x => x.denied) }),
            byScope = rows.GroupBy(r => r.scope).Select(g => new { scope = g.Key, count = g.Sum(x => x.count), denied = g.Sum(x => x.denied) }).OrderByDescending(x => x.count),
            k.LastUsedAt, k.LastUsedScope, k.UsageCount,
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var k = await _db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (k is null) return NotFound();
        k.RevokedAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/* ======================================================================
 * Açık API v1 — X-Api-Key ile. Salt okunur; anahtarın kiracısı ve
 * yetkileri (scope) dışına çıkamaz. Dakikada 120 istek sınırı.
 * ==================================================================== */
[ApiController]
[Route("api/public/v1")]
[AllowAnonymous]
public class PublicApiController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Window)> Rate = new();
    private readonly Sql _sql;
    private readonly AppCache _cache;
    private readonly PeopleDirectory _people;
    private readonly GovernanceDbContext _db;
    private Guid _keyId;
    public PublicApiController(Sql sql, AppCache cache, PeopleDirectory people, GovernanceDbContext db) { _sql = sql; _cache = cache; _people = people; _db = db; }

    private async Task<(string? Tenant, IActionResult? Error)> AuthorizeAsync(string scope, CancellationToken ct)
    {
        var key = Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return (null, Unauthorized(new { message = "X-Api-Key başlığı gerekli." }));
        var hash = ApiKeysController.Hash(key);
        var row = (await _sql.QueryAsync("""
            SELECT k."Id", k."TenantSlug", k."Scopes", t."Status", k."ExpiresAt" FROM governance_api_keys k
            JOIN platform_tenants t ON t."Slug" = k."TenantSlug"
            WHERE k."KeyHash" = $1 AND k."RevokedAt" IS NULL
            """, r => (Id: r.GetGuid(0), Tenant: r.GetString(1), Scopes: r.GetFieldValue<string[]>(2), Status: r.GetString(3), ExpiresAt: r.Ts(4)), ct, hash)).FirstOrDefault();
        if (row.Tenant is null) return (null, Unauthorized(new { message = "Geçersiz veya iptal edilmiş anahtar." }));
        if (ApiScopes.Inactive(null, row.ExpiresAt, DateTime.UtcNow) is not null)
            return (null, Unauthorized(new { message = "Anahtarın süresi dolmuş.", code = "key_expired" }));
        if (row.Status != "Active") return (null, StatusCode(403, new { message = "Şirket hesabı aktif değil." }));
        if (!ApiScopes.Allows(row.Scopes, scope))
        {
            await RecordUsageAsync(row.Id, row.Tenant, scope, false, ct);
            return (null, StatusCode(403, new { message = $"Anahtarın '{scope}' yetkisi yok.", code = "scope_denied" }));
        }

        // Dakikalık sınır: Redis varsa tüm servis kopyaları ortak sayar, yoksa bu kopyanın belleği.
        var now = DateTime.UtcNow;
        var count = await _cache.CountInWindowAsync("public-api-rate", hash, TimeSpan.FromMinutes(1))
            ?? Rate.AddOrUpdate(hash, _ => (1, now), (_, v) => now - v.Window > TimeSpan.FromMinutes(1) ? (1, now) : (v.Count + 1, v.Window)).Count;
        Response.Headers["X-RateLimit-Limit"] = "120";
        Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, 120 - count).ToString();
        if (count > 120)
        {
            await RecordUsageAsync(row.Id, row.Tenant, scope, false, ct);
            return (null, StatusCode(429, new { message = "Dakikalık istek sınırı aşıldı." }));
        }
        await RecordUsageAsync(row.Id, row.Tenant, scope, true, ct);
        _keyId = row.Id;
        return (row.Tenant, null);
    }

    /// <summary>
    /// Dalga 12: anahtar × gün × yetki sayacı ve son kullanım. Kullanım istatistiği yazılamazsa
    /// (ör. tablo henüz yok) istek yine de yanıtlanır.
    /// </summary>
    private async Task RecordUsageAsync(Guid keyId, string tenant, string scope, bool allowed, CancellationToken ct)
    {
        try
        {
            if (allowed)
                await _sql.ExecuteAsync("UPDATE governance_api_keys SET \"LastUsedAt\" = now(), \"UsageCount\" = \"UsageCount\" + 1, \"LastUsedScope\" = $2 WHERE \"Id\" = $1", ct, keyId, scope);
            await _sql.ExecuteAsync("""
                INSERT INTO governance_api_key_usage ("Id","TenantSlug","KeyId","Day","Scope","Count","DeniedCount","UpdatedAt")
                VALUES ($1,$2,$3,current_date,$4,$5,$6,now())
                ON CONFLICT ("KeyId","Day","Scope") DO UPDATE SET "Count" = governance_api_key_usage."Count" + EXCLUDED."Count",
                    "DeniedCount" = governance_api_key_usage."DeniedCount" + EXCLUDED."DeniedCount", "UpdatedAt" = now()
                """, ct, Guid.NewGuid(), tenant, keyId, scope, allowed ? 1 : 0, allowed ? 0 : 1);
        }
        catch (Npgsql.PostgresException) { /* istatistik en iyi çaba */ }
    }

    /* ------------------------------------------------------------------ G29 REST hook (Zapier / n8n) */

    /// <summary>Zapier "hookUrl", n8n/diğerleri "target_url" / "url" gönderir; hepsi kabul edilir.</summary>
    public sealed class HookInput
    {
        [System.Text.Json.Serialization.JsonPropertyName("target_url")] public string? TargetUrlSnake { get; set; }
        public string? TargetUrl { get; set; }
        public string? HookUrl { get; set; }
        public string? Url { get; set; }
        public string? Event { get; set; }
        public List<string>? Events { get; set; }
        public string? Name { get; set; }
        /// <summary>n8n: kiracı hedefin kendi sunucusu olduğunu beyan eder (bilgi amaçlı; bilinen yurt dışı bulut adreslerini serbest bırakmaz).</summary>
        [System.Text.Json.Serialization.JsonPropertyName("self_hosted")] public bool? SelfHosted { get; set; }
        public string? Target => TargetUrlSnake ?? TargetUrl ?? HookUrl ?? Url;
    }

    /// <summary>Hedef sınıfı: zapier | n8n-cloud | internal (iç ağ) | self-hosted (kiracının kendi sunucusu).</summary>
    public static string Deployment(string url) => TransferGuard.HookProvider(url) switch
    {
        "zapier" => "zapier",
        "n8n" => "n8n-cloud",
        _ => TransferGuard.IsInternalHost(url) ? "internal" : "self-hosted",
    };

    private static object HookView(Webhook w) => new
    {
        id = w.Id, target_url = w.Url, @event = w.Events.FirstOrDefault(), events = w.Events, name = w.Name, enabled = w.IsEnabled,
        createdAt = w.CreatedAt, signingSecret = (string?)null, deployment = Deployment(w.Url),
    };

    /// <summary>Abone ol: POST /hooks {target_url|hookUrl, event}. Yanıttaki id ile DELETE /hooks/{id}.</summary>
    [HttpPost("hooks")]
    public async Task<IActionResult> Subscribe([FromBody] HookInput body, CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("hooks:write", ct);
        if (err is not null) return err;
        var url = body.Target?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return BadRequest(new { message = "target_url geçerli bir http(s) adresi olmalı.", code = "invalid_target" });
        // SSRF: kiracının kendi iç ağı serbest; yerel makine, bulut meta veri ve HR360 altyapısı değil.
        if (WebhookTargetGuard.ValidateRestHook(url) is { } invalid)
            return BadRequest(new { message = invalid, code = "invalid_target" });
        var events = (body.Events ?? new()).Concat(body.Event is null ? Array.Empty<string>() : new[] { body.Event }).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
        if (events.Count == 0 || events.Any(e => EventCatalog.Types.All(t => t.Type != e)))
            return BadRequest(new { message = "Geçerli bir olay (event) gerekli.", code = "invalid_event", events = EventCatalog.Types.Select(t => t.Type) });
        // KVKK m.9 (K4 kilidi): Zapier (ABD) ve n8n Cloud (AB) yurt dışıdır; aktarım dayanağı kaydı yoksa abonelik açılmaz.
        // Kendi sunucunuzdaki n8n (iç ağ ya da kendi alan adınız) kiracının altyapısıdır, aktarım sayılmaz.
        if (TransferGuard.HookProvider(url) is { } provider && await TransferGuard.MissingAsync(_db, tenant!, provider, ct) is { } msg)
            return Conflict(new
            {
                message = msg, code = "transfer_basis_required", provider,
                hint = body.SelfHosted == true ? "self_hosted yalnızca kendi sunucunuzdaki adresler içindir; n8n.cloud / zapier.com yurt dışı hizmettir." : null,
            });
        var count = Convert.ToInt32(await _sql.ScalarAsync("SELECT count(*)::int FROM governance_webhooks WHERE \"TenantSlug\" = $1 AND \"Source\" = 'rest-hook'", ct, tenant));
        if (count >= 100) return StatusCode(429, new { message = "En fazla 100 REST hook aboneliği açılabilir.", code = "too_many_hooks" });
        var w = new Webhook
        {
            TenantSlug = tenant!, Name = string.IsNullOrWhiteSpace(body.Name) ? $"REST hook: {string.Join(", ", events)} ({uri.Host})" : body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 120)],
            Url = url!, Events = events, IsEnabled = true, Secret = WebhooksController.NewSecret(), Source = "rest-hook", ApiKeyId = _keyId,
        };
        _db.Webhooks.Add(w);
        await _db.SaveChangesAsync(ct);
        Response.Headers["Location"] = $"/api/governance/public/v1/hooks/{w.Id}";
        // İmza anahtarı (X-HR360-Signature doğrulaması için) yalnızca bu yanıtta bir kez döner.
        return StatusCode(201, new
        {
            id = w.Id, target_url = w.Url, @event = events[0], events, name = w.Name, enabled = true, createdAt = w.CreatedAt, signingSecret = w.Secret,
            deployment = Deployment(w.Url),
        });
    }

    [HttpGet("hooks")]
    public async Task<IActionResult> Hooks(CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("hooks:write", ct);
        if (err is not null) return err;
        var list = await _db.Webhooks.IgnoreQueryFilters().AsNoTracking().Where(w => w.TenantSlug == tenant && w.Source == "rest-hook").OrderBy(w => w.CreatedAt).ToListAsync(ct);
        return Ok(new { data = list.Select(HookView) });
    }

    /// <summary>Aboneliği kaldır (Zapier "unsubscribe"). Yalnızca REST hook kayıtları silinebilir.</summary>
    [HttpDelete("hooks/{id:guid}")]
    public async Task<IActionResult> Unsubscribe(Guid id, CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("hooks:write", ct);
        if (err is not null) return err;
        var w = await _db.Webhooks.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.TenantSlug == tenant && x.Source == "rest-hook", ct);
        if (w is null) return NotFound(new { message = "Abonelik bulunamadı." });
        _db.Webhooks.Remove(w);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id, deleted = true });
    }

    /// <summary>
    /// Olay başına örnek veri (Zapier "perform list" / n8n alan eşleme). Gerçek kişisel veri
    /// DÖNMEZ: teslimat zarfıyla aynı biçimde, uydurma değerlerle sentetik örnek.
    /// </summary>
    [HttpGet("hooks/samples/{eventType}")]
    public async Task<IActionResult> Sample(string eventType, CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("hooks:write", ct);
        if (err is not null) return err;
        var t = EventCatalog.Types.FirstOrDefault(x => x.Type == eventType);
        if (t.Type is null || t.Type == "*") return NotFound(new { message = "Bilinmeyen olay.", events = EventCatalog.Types.Where(x => x.Type != "*").Select(x => x.Type) });
        return Ok(new[] { HookSamples.Envelope(tenant!, t.Type, t.Fields) });
    }

    [HttpGet("hooks/events")]
    public async Task<IActionResult> HookEvents(CancellationToken ct)
    {
        var (_, err) = await AuthorizeAsync("hooks:write", ct);
        if (err is not null) return err;
        return Ok(new { data = EventCatalog.Types.Select(t => new { key = t.Type, label = t.Label, fields = t.Fields }) });
    }

    [HttpGet("employees")]
    public async Task<IActionResult> Employees([FromQuery] string? status, CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("employees:read", ct);
        if (err is not null) return err;
        var people = await _people.ListAsync(tenant!, ct, includeTerminated: status == "all");
        return Ok(new { data = people.Select(p => new { id = p.Id, name = p.Name, email = p.Email, position = p.Position, departmentId = p.DepartmentId, department = p.Department, hireDate = p.HireDate, status = p.Status }) });
    }

    [HttpGet("departments")]
    public async Task<IActionResult> Departments(CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("departments:read", ct);
        if (err is not null) return err;
        var rows = await _sql.QueryAsync("SELECT \"Id\", \"Name\", \"ParentDepartmentId\", \"HeadEmployeeId\" FROM organization_departments WHERE \"TenantSlug\" = $1 ORDER BY 2",
            r => new { id = r.GetGuid(0), name = r.GetString(1), parentId = r.GuidOrNull(2), headEmployeeId = r.GuidOrNull(3) }, ct, tenant);
        return Ok(new { data = rows });
    }

    [HttpGet("leaves")]
    public async Task<IActionResult> Leaves([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("leaves:read", ct);
        if (err is not null) return err;
        var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90));
        var rows = await _sql.QueryAsync("""
            SELECT "Id", "EmployeeId", "Type", "StartDate", "EndDate", "Days", "Status" FROM leave_requests
            WHERE "TenantSlug" = $1 AND "StartDate" <= $3 AND "EndDate" >= $2 AND "Status" IN ('Approved','Submitted') ORDER BY "StartDate"
            """, r => new
        {
            id = r.GetGuid(0), employeeId = r.GetGuid(1), type = r.GetString(2), startDate = r.GetFieldValue<DateOnly>(3),
            endDate = r.GetFieldValue<DateOnly>(4), days = r.GetDecimal(5), status = r.GetString(6)
        }, ct, tenant, f, t);
        return Ok(new { data = rows });
    }

    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] DateTime? since, CancellationToken ct)
    {
        var (tenant, err) = await AuthorizeAsync("events:read", ct);
        if (err is not null) return err;
        var s = since is null ? DateTime.UtcNow.AddDays(-1) : DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);
        var rows = await _sql.QueryAsync("""
            SELECT "Id", "EventType", "Payload"::text, "OccurredAt" FROM governance_events
            WHERE "TenantSlug" = $1 AND "OccurredAt" > $2 ORDER BY "OccurredAt" LIMIT 500
            """, r => new { id = r.GetGuid(0), type = r.GetString(1), data = r.IsDBNull(2) ? (JsonElement?)null : EventHub.Sanitize(JsonDocument.Parse(r.GetString(2)).RootElement.Clone()), occurredAt = r.GetFieldValue<DateTime>(3) },
            ct, tenant, s);
        // Gizli alanlar (onay jetonu vb.) açık API'ye de verilmez; eski kayıtlar göçle de temizlendi.
        return Ok(new { data = rows });
    }

    [HttpGet("openapi.json")]
    public IActionResult Spec() => Ok(new
    {
        openapi = "3.0.3",
        info = new { title = "HR360 Açık API", version = "1.0", description = "X-Api-Key başlığı ile kimlik doğrulama. Salt okunur. \"read-only\" yetkisi tüm okuma uçlarını kapsar (hooks:write hariç). Süresi dolmuş anahtar 401 (code: key_expired), yetkisiz uç 403 (code: scope_denied) döner." },
        servers = new[] { new { url = "/api/governance/public/v1" } },
        components = new { securitySchemes = new { apiKey = new { type = "apiKey", @in = "header", name = "X-Api-Key" } } },
        security = new[] { new Dictionary<string, string[]> { ["apiKey"] = Array.Empty<string>() } },
        paths = new Dictionary<string, object>
        {
            ["/employees"] = new { get = new { summary = "Çalışanlar (scope: employees:read)", parameters = new[] { new { name = "status", @in = "query", schema = new { type = "string", @enum = new[] { "all" } } } } } },
            ["/departments"] = new { get = new { summary = "Departmanlar (scope: departments:read)" } },
            ["/leaves"] = new { get = new { summary = "İzinler (scope: leaves:read)", parameters = new[] { new { name = "from", @in = "query", schema = new { type = "string", format = "date" } }, new { name = "to", @in = "query", schema = new { type = "string", format = "date" } } } } },
            ["/events"] = new { get = new { summary = "Olay akışı (scope: events:read)", parameters = new[] { new { name = "since", @in = "query", schema = new { type = "string", format = "date-time" } } } } },
            ["/hooks"] = new
            {
                get = new { summary = "REST hook abonelikleri (scope: hooks:write)" },
                post = new { summary = "REST hook aboneliği — Zapier/n8n (scope: hooks:write). Gövde: {\"target_url\": \"https://...\", \"event\": \"leave.approved\"}. zapier.com hedefleri KVKK aktarım dayanağı ister (409 transfer_basis_required)." },
            },
            ["/hooks/{id}"] = new { delete = new { summary = "Aboneliği kaldır (scope: hooks:write)" } },
            ["/hooks/samples/{event}"] = new { get = new { summary = "Olayın sentetik örnek yükü (scope: hooks:write)" } },
            ["/hooks/events"] = new { get = new { summary = "Abone olunabilen olaylar (scope: hooks:write)" } },
        },
    });
}

/// <summary>REST hook örnek verisi: teslimat zarfıyla aynı biçim, sentetik değerler (kişisel veri yok).</summary>
public static class HookSamples
{
    public static object Envelope(string tenant, string type, string[] fields)
    {
        var data = new Dictionary<string, object?> { ["TenantSlug"] = tenant };
        foreach (var f in fields)
            data[f] = f switch
            {
                _ when f.EndsWith("Id") => "00000000-0000-4000-8000-000000000001",
                "FirstName" => "Örnek",
                "LastName" => "Çalışan",
                "RequesterName" => "Örnek Çalışan",
                "Email" => "ornek.calisan@example.com",
                "HireDate" or "StartDate" or "EffectiveFrom" or "EffectiveDate" => "2026-01-05",
                "EndDate" => "2026-01-09",
                "SignedAt" => "2026-01-05T09:30:00.000Z",
                "Days" => 5,
                "Type" => "Annual",
                "Method" => "OTP-InApp",
                "DocumentType" => "DocumentRequest",
                "TemplateName" => "Çalışma belgesi",
                "DocumentSha256" => new string('0', 64),
                "OldStatus" => "Active",
                "NewStatus" => "OnLeave",
                "PositionTitle" => "Örnek pozisyon",
                "WorkflowType" => "Leave",
                "Subject" => "Örnek talep",
                _ => "örnek",
            };
        return new { id = "00000000-0000-4000-8000-0000000000aa", type, tenant, occurredAt = "2026-01-05T09:30:00Z", data, sample = true };
    }
}

/* ======================================================================
 * Slack / Microsoft Teams: gelen webhook'larla kanala bildirim + Slack
 * slash komutu (/hr360 izindekiler | kimnerede | bekleyen).
 * ==================================================================== */
[Route("api/integrations")]
[Authorize(Policy = "RequireHrAdmin")]
public class IntegrationsController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly Dispatcher _dispatcher;
    public IntegrationsController(GovernanceDbContext db, Dispatcher dispatcher) { _db = db; _dispatcher = dispatcher; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _db.Integrations.AsNoTracking().OrderBy(i => i.Kind).ThenBy(i => i.Name).ToListAsync(ct))
            .Select(i => new { i.Id, i.Kind, i.Name, webhookUrl = MaskUrl(i.WebhookUrl), i.Events, i.IsEnabled, i.LastStatus, i.CreatedAt, hasSigningSecret = i.SigningSecret != null }));

    private static string MaskUrl(string url) => url.Length <= 32 ? url : url[..28] + "…" + url[^4..];

    public record IntegrationInput(string Kind, string Name, string WebhookUrl, List<string> Events, string? SigningSecret, bool IsEnabled);

    [HttpPost]
    public async Task<IActionResult> Create(IntegrationInput body, CancellationToken ct)
    {
        if (body.Kind is not ("Slack" or "Teams")) return BadRequest(new { message = "Tür Slack veya Teams olmalı." });
        if (!Uri.TryCreate(body.WebhookUrl, UriKind.Absolute, out _)) return BadRequest(new { message = "Gelen webhook adresi geçersiz." });
        var i = new Integration { Kind = body.Kind, Name = body.Name.Trim(), WebhookUrl = body.WebhookUrl.Trim(), Events = body.Events, SigningSecret = body.SigningSecret, IsEnabled = body.IsEnabled };
        _db.Integrations.Add(i);
        await _db.SaveChangesAsync(ct);
        return Ok(new { i.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, IntegrationInput body, CancellationToken ct)
    {
        var i = await _db.Integrations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        i.Name = body.Name.Trim();
        if (!string.IsNullOrWhiteSpace(body.WebhookUrl) && !body.WebhookUrl.Contains('…')) i.WebhookUrl = body.WebhookUrl.Trim();
        i.Events = body.Events;
        if (body.SigningSecret is not null) i.SigningSecret = body.SigningSecret.Length == 0 ? null : body.SigningSecret;
        i.IsEnabled = body.IsEnabled;
        await _db.SaveChangesAsync(ct);
        return Ok(new { i.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var i = await _db.Integrations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        _db.Integrations.Remove(i);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var i = await _db.Integrations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        i.LastStatus = await _dispatcher.PostChatAsync(i.Kind, i.WebhookUrl, "Bağlantı testi — bu kanal HR360 bildirimlerini alacak.", "test", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { i.LastStatus, ok = i.LastStatus is >= 200 and < 300 });
    }

    /// <summary>Slack slash komutu. Slack uygulamasında Request URL olarak bu adres verilir.</summary>
    [HttpPost("{id:guid}/slack-command")]
    [AllowAnonymous]
    [BufferBody]
    public async Task<IActionResult> SlackCommand(Guid id, CancellationToken ct)
    {
        // Gövde model bağlamadan önce tamponlandı (BufferBody); imza ham gövdeyle doğrulanır.
        Request.Body.Position = 0;
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
            raw = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;
        var row = (await Db.QueryAsync("SELECT \"TenantSlug\", \"SigningSecret\" FROM governance_integrations WHERE \"Id\" = $1 AND \"Kind\" = 'Slack' AND \"IsEnabled\"",
            r => (Tenant: r.GetString(0), Secret: r.Str(1)), ct, id)).FirstOrDefault();
        if (row.Tenant is null) return NotFound();
        if (row.Secret is not null)
        {
            var ts = Request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault() ?? "";
            var sig = Request.Headers["X-Slack-Signature"].FirstOrDefault() ?? "";
            if (!long.TryParse(ts, out var t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300) return Unauthorized();
            var expected = "v0=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(row.Secret), Encoding.UTF8.GetBytes($"v0:{ts}:{raw}"))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(sig))) return Unauthorized();
        }
        var form = Request.HasFormContentType ? await Request.ReadFormAsync(ct) : null;
        var text = (form?["text"].ToString() ?? "").Trim().ToLowerInvariant();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        string reply;
        switch (text)
        {
            case "izindekiler":
                var onLeave = await Db.QueryAsync("""
                    SELECT e."FirstName" || ' ' || e."LastName", l."EndDate" FROM leave_requests l JOIN employee_employees e ON e."Id" = l."EmployeeId"
                    WHERE l."TenantSlug" = $1 AND l."Status" = 'Approved' AND l."StartDate" <= $2 AND l."EndDate" >= $2 ORDER BY 1
                    """, r => $"• {r.GetString(0)} ({r.GetFieldValue<DateOnly>(1):dd.MM}'e kadar)", ct, row.Tenant, today);
                reply = onLeave.Count == 0 ? "Bugün izinde olan kimse yok." : $"*Bugün izinde ({onLeave.Count}):*\n" + string.Join("\n", onLeave);
                break;
            case "kimnerede":
                var modes = await Db.QueryAsync("SELECT \"Mode\", count(*) FROM engagement_presence WHERE \"TenantSlug\" = $1 AND \"Date\" = $2 GROUP BY 1",
                    r => (Mode: r.GetString(0), N: r.GetInt64(1)), ct, row.Tenant, today);
                string L(string m) => m switch { "Office" => "Ofiste", "Remote" => "Uzaktan", "Travel" => "Seyahatte", "Off" => "Çalışmıyor", _ => m };
                reply = modes.Count == 0 ? "Bugün için çalışma yeri bildiren olmadı." : "*Bugün:* " + string.Join(" · ", modes.Select(m => $"{L(m.Mode)} {m.N}"));
                break;
            case "bekleyen":
                var pending = Convert.ToInt64(await Db.ScalarAsync("SELECT count(*) FROM workflow_requests WHERE \"TenantSlug\" = $1 AND \"Status\" = 'Pending'", ct, row.Tenant));
                reply = $"Şirkette onay bekleyen *{pending}* talep var.";
                break;
            default:
                reply = "Kullanım: `/hr360 izindekiler` · `/hr360 kimnerede` · `/hr360 bekleyen`";
                break;
        }
        return Ok(new { response_type = "ephemeral", text = reply });
    }
}
