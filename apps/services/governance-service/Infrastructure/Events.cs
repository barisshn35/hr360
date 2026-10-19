using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Models;
using GovernanceService.Tenancy;

namespace GovernanceService.Infrastructure;

/// <summary>Canlı olay radarında gösterilen olay.</summary>
public sealed record RadarEvent(Guid Id, string? TenantSlug, string Topic, string EventType, JsonElement? Payload, DateTime OccurredAt, string Summary);

/// <summary>
/// Süreç içi yayın: Kafka tüketicisi her yeni olayı buraya yazar, açık SSE
/// bağlantıları (EventsController.Stream) okur. Yavaş istemci diğerlerini
/// bekletmesin diye her abonenin sınırlı, eskiyi düşüren kendi kanalı var.
/// </summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<RadarEvent>> _subs = new();

    public (Guid Id, ChannelReader<RadarEvent> Reader) Subscribe()
    {
        var ch = Channel.CreateBounded<RadarEvent>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subs[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subs.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public int Subscribers => _subs.Count;

    public void Publish(RadarEvent e)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(e);
    }

    /// <summary>Olay tipini ve yükünü tek satırlık Türkçe cümleye çevirir.</summary>
    public static string Describe(string type, JsonElement? p)
    {
        string F(string name) => Field(p, name) ?? "";
        // Olay yükündeki ISO tarih ve noktalı ondalık, kullanıcıya Türkçe biçimde gösterilir.
        static string TrDate(string v) => DateOnly.TryParse(v.Length >= 10 ? v[..10] : v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d.ToString("dd.MM.yyyy") : v;
        static string TrNumber(string v) => decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) : v;
        var person = $"{F("FirstName")} {F("LastName")}".Trim();
        if (person.Length == 0) person = F("RequesterName");
        if (person.Length == 0) person = F("EmployeeName");
        if (person.Length == 0) person = "Bir çalışan";
        return type switch
        {
            "employee.hired" => $"{person} işe alındı",
            "employee.assigned" => $"{person} yeni görevine atandı{(F("PositionTitle") is { Length: > 0 } pos ? $": {pos}" : "")}",
            "employee.status-changed" => $"{person} durumu: {F("OldStatus")} → {F("NewStatus")}",
            "workflow.submitted" => $"{person} onaya gönderdi: {F("Subject")}",
            "workflow.approved" => $"Onaylandı: {F("Subject")}".TrimEnd(':', ' '),
            "workflow.rejected" => $"Reddedildi: {F("Subject")}".TrimEnd(':', ' '),
            "workflow.step-approved" => $"Onay adımı geçti: {F("Subject")}".TrimEnd(':', ' '),
            "leave.approved" => $"İzin onaylandı ({TrNumber(F("Days"))} gün, {TrDate(F("StartDate"))})",
            "leave.cancelled" => "İzin iptal edildi",
            "leave.rejected" => "İzin reddedildi",
            "document.signed" => $"Belge imzalandı (basit e-imza): {F("TemplateName")}".TrimEnd(':', ' '),
            _ => type,
        };
    }

    /// <summary>
    /// GÜVENLİK: Olay yükünden gizli alanları (adında token/secret/password geçen; ör.
    /// workflow.submitted'daki tek kullanımlık e-posta karar jetonu "ActionToken") iç içe dahil
    /// ayıklar. Önceden jeton governance_events'e ham yazılıyor, olay radarında ve açık API'de
    /// (events:read) okunabiliyordu: okuyan kişi başkasının onay adımını e-posta bağlantısıyla
    /// karara bağlayabilirdi. <paramref name="personal"/> verilirse bu alanlar da (ör. onaycı
    /// e-postası; radarda gereksiz kişisel veri) ayıklanır.
    /// </summary>
    public static JsonElement? Sanitize(JsonElement? payload, params string[] personal)
    {
        if (payload is not { } p || (p.ValueKind != JsonValueKind.Object && p.ValueKind != JsonValueKind.Array)) return payload;
        var node = System.Text.Json.Nodes.JsonNode.Parse(p.GetRawText());
        if (node is null || !Strip(node, personal)) return payload;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    public static bool IsSecretName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("token") || n.Contains("secret") || n.Contains("password") || n.Contains("passwd");
    }

    private static bool Strip(System.Text.Json.Nodes.JsonNode node, string[] personal)
    {
        var changed = false;
        if (node is System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var key in o.Select(kv => kv.Key).ToList())
            {
                if (IsSecretName(key) || personal.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)))
                {
                    o.Remove(key);
                    changed = true;
                }
                else if (o[key] is { } child && Strip(child, personal)) changed = true;
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray a)
        {
            foreach (var child in a) if (child is not null && Strip(child, personal)) changed = true;
        }
        return changed;
    }

    /// <summary>Saklanan/radarda gösterilen yükten ayrıca çıkarılan kişisel alanlar.</summary>
    public static readonly string[] RadarPersonalFields = { "ApproverEmail" };

    /// <summary>Yükten alan okur: büyük/küçük harf duyarsız, "a.b" ile iç içe.</summary>
    public static string? Field(JsonElement? payload, string path)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } cur) return null;
        foreach (var part in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            var found = false;
            foreach (var prop in cur.EnumerateObject())
            {
                if (string.Equals(prop.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    cur = prop.Value;
                    found = true;
                    break;
                }
            }
            if (!found) return null;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => cur.GetRawText(),
        };
    }
}

/// <summary>
/// Tüm hr360.* konularını dinler (kendi tüketici grubu — notification-service'i
/// etkilemez). Her olay governance_events'e bir kez yazılır (event-id birincil
/// anahtar = idempotency), radara yayınlanır ve kural/webhook/entegrasyon
/// gönderimi için Dispatcher'a verilir.
/// </summary>
public sealed class EventConsumer : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly EventHub _hub;
    private readonly ILogger<EventConsumer> _log;

    public EventConsumer(IServiceProvider sp, EventHub hub, ILogger<EventConsumer> log) { _sp = sp; _hub = hub; _log = log; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => Loop(stoppingToken), stoppingToken);

    private async Task Loop(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "kafka:9092",
            GroupId = "governance-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            TopicMetadataRefreshIntervalMs = 10_000,
        };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var consumer = new ConsumerBuilder<string, string>(config)
                    .SetErrorHandler((_, e) => _log.LogDebug("Kafka: {Reason}", e.Reason)).Build();
                consumer.Subscribe("^hr360\\..*");
                _log.LogInformation("Olay radarı Kafka'yı dinliyor (hr360.*)");
                while (!ct.IsCancellationRequested)
                {
                    var r = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (r?.Message is null) continue;
                    try { await HandleAsync(r, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "Olay işlenemedi, atlanıyor: {Offset}", r.TopicPartitionOffset);
                    }
                    consumer.Commit(r);
                }
                consumer.Close();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Kafka tüketicisi yeniden başlatılıyor");
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
    }

    private async Task HandleAsync(ConsumeResult<string, string> r, CancellationToken ct)
    {
        string? H(string key) => r.Message.Headers?.TryGetLastBytes(key, out var b) == true ? Encoding.UTF8.GetString(b) : null;
        var type = H("event-type") ?? "unknown";
        var id = Guid.TryParse(H("event-id"), out var g) ? g : DeterministicId(r);
        JsonElement? payload = null;
        try { payload = JsonDocument.Parse(r.Message.Value).RootElement.Clone(); } catch (JsonException) { }
        // Gizli alanlar (e-posta karar jetonu vb.) hiçbir yere (kural, webhook, sohbet) gitmez.
        payload = EventHub.Sanitize(payload);
        // Saklanan ve radarda gösterilen kopyada onaycı e-postası da yok (sohbet bildirimi
        // onaycıyı e-postayla eşlediği için Dispatcher'a giden kopyada kalır).
        var stored = EventHub.Sanitize(payload, EventHub.RadarPersonalFields);
        var tenant = EventHub.Field(payload, "TenantSlug");
        var at = r.Message.Timestamp.UtcDateTime;
        if (at.Year < 2000) at = DateTime.UtcNow;

        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
        var sql = scope.ServiceProvider.GetRequiredService<Sql>();
        var inserted = await sql.ExecuteAsync(
            """
            INSERT INTO governance_events ("Id","TenantSlug","Topic","EventType","Payload","OccurredAt")
            VALUES ($1,$2,$3,$4,$5::jsonb,$6) ON CONFLICT ("Id") DO NOTHING
            """, ct, id, string.IsNullOrEmpty(tenant) ? null : tenant, r.Topic, type,
            stored is null ? null : stored.Value.GetRawText(), at);
        if (inserted == 0) return; // daha önce işlendi

        _hub.Publish(new RadarEvent(id, tenant, r.Topic, type, stored, at, EventHub.Describe(type, stored)));
        if (!string.IsNullOrEmpty(tenant))
            await scope.ServiceProvider.GetRequiredService<Dispatcher>().DispatchAsync(scope.ServiceProvider, tenant, id, type, payload, ct);
    }

    private static Guid DeterministicId(ConsumeResult<string, string> r) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{r.Topic}:{r.Partition.Value}:{r.Offset.Value}")));
}

/// <summary>
/// Olay → kural motoru, giden webhook'lar ve Slack/Teams gönderimi.
/// Hepsi "en iyi çaba": bir hedefin hatası diğerlerini etkilemez, sonuç
/// governance_rule_runs / governance_webhook_deliveries'e yazılır.
/// </summary>
public sealed class Dispatcher
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Dispatcher> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Dispatcher(IHttpClientFactory http, ILogger<Dispatcher> log) { _http = http; _log = log; }

    public async Task DispatchAsync(IServiceProvider sp, string tenant, Guid eventId, string type, JsonElement? payload, CancellationToken ct)
    {
        var db = sp.GetRequiredService<GovernanceDbContext>();
        var notifier = sp.GetRequiredService<Notifier>();
        var summary = EventHub.Describe(type, payload);

        // ------------------------------------------------------------ kurallar
        var rules = await db.Rules.Where(x => x.TenantSlug == tenant && x.IsEnabled && (x.Trigger == type || x.Trigger == "*")).ToListAsync(ct);
        foreach (var rule in rules)
        {
            var (matched, why) = Evaluate(rule.Conditions, payload);
            if (!matched) continue;
            var results = new List<string>();
            foreach (var action in rule.Actions)
            {
                try { results.Add(await ExecuteAsync(db, notifier, tenant, rule, action, type, payload, summary, ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { results.Add($"{action.Type}: hata ({ex.Message})"); }
            }
            rule.FireCount++;
            rule.LastFiredAt = DateTime.UtcNow;
            db.RuleRuns.Add(new RuleRun { TenantSlug = tenant, RuleId = rule.Id, RuleName = rule.Name, EventType = type, Result = string.Join("; ", results) });
        }

        // ------------------------------------------------------------ webhook'lar
        var hooks = await db.Webhooks.Where(w => w.TenantSlug == tenant && w.IsEnabled).ToListAsync(ct);
        foreach (var hook in hooks.Where(h => h.Events.Contains(type) || h.Events.Contains("*")))
            await DeliverWebhookAsync(db, hook, eventId, type, payload, ct);

        // ------------------------------------------------------------ Slack / Teams
        var integrations = await db.Integrations.Where(i => i.TenantSlug == tenant && i.IsEnabled).ToListAsync(ct);
        foreach (var integ in integrations.Where(i => i.Events.Contains(type) || i.Events.Contains("*")))
            integ.LastStatus = await PostChatAsync(integ.Kind, integ.WebhookUrl, summary, type, ct);

        // ------------------------------------------------------------ Slack / Teams uygulamaları (kişiye özel, düğmeli)
        try { await sp.GetRequiredService<Chat.ChatService>().OnEventAsync(db, tenant, type, payload, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Sohbet uygulaması bildirimi başarısız ({Type})", type); }

        // ------------------------------------------------------------ Google / Outlook takvimleri (onaylanan izinler)
        try { await sp.GetRequiredService<Calendar.CalendarService>().OnEventAsync(db, tenant, type, payload, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Takvim senkronizasyonu başarısız ({Type})", type); }

        // Kural, webhook ya da entegrasyon olay işlenirken silinmiş olabilir (yönetici sildi): o kaydın
        // sayaç/durum güncellemesi atlanır, olayın geri kalan kayıtları (çalıştırma geçmişi vb.) yazılır.
        for (var attempt = 0; ; attempt++)
        {
            try { await db.SaveChangesAsync(ct); break; }
            catch (DbUpdateConcurrencyException ex) when (attempt < 3)
            {
                foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
                _log.LogInformation("Olay işlenirken silinen {Count} kayıt güncellemesi atlandı ({Type})", ex.Entries.Count, type);
            }
        }
    }

    public static (bool, string) Evaluate(IEnumerable<RuleCondition> conditions, JsonElement? payload)
    {
        foreach (var c in conditions)
        {
            var actual = EventHub.Field(payload, c.Field);
            var ok = Compare(actual, c.Op, c.Value);
            if (!ok) return (false, $"{c.Field} {c.Op} {c.Value} sağlanmadı (değer: {actual ?? "yok"})");
        }
        return (true, "tüm koşullar sağlandı");
    }

    private static bool Compare(string? actual, string op, string expected)
    {
        if (actual is null) return op == "neq";
        var inv = CultureInfo.InvariantCulture;
        var numeric = decimal.TryParse(actual, NumberStyles.Any, inv, out var a) & decimal.TryParse(expected, NumberStyles.Any, inv, out var e);
        return op switch
        {
            "eq" => numeric ? a == e : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "neq" => numeric ? a != e : !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "gt" => numeric && a > e,
            "gte" => numeric && a >= e,
            "lt" => numeric && a < e,
            "lte" => numeric && a <= e,
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static string Render(string template, JsonElement? payload, string summary) =>
        System.Text.RegularExpressions.Regex.Replace(template.Replace("{{ozet}}", summary), @"\{\{\s*([\w.]+)\s*\}\}",
            m => EventHub.Field(payload, m.Groups[1].Value) ?? m.Value);

    private async Task<string> ExecuteAsync(GovernanceDbContext db, Notifier notifier, string tenant, Rule rule, RuleAction action,
        string type, JsonElement? payload, string summary, CancellationToken ct)
    {
        var message = Render(string.IsNullOrWhiteSpace(action.Message) ? "{{ozet}}" : action.Message, payload, summary);
        switch (action.Type)
        {
            case "notify":
                var target = action.Target?.Trim() ?? "requester";
                var field = target switch
                {
                    "requester" => EventHub.Field(payload, "RequesterEmployeeId") ?? EventHub.Field(payload, "EmployeeId"),
                    "approver" => EventHub.Field(payload, "ApproverEmployeeId"),
                    "employee" => EventHub.Field(payload, "EmployeeId"),
                    _ => target,
                };
                if (!Guid.TryParse(field, out var emp)) return "notify: alıcı bulunamadı";
                await notifier.InAppAsync(tenant, emp, rule.Name, message, "governance.rule", ct);
                return "notify: gönderildi";
            case "webhook":
                if (string.IsNullOrWhiteSpace(action.Target)) return "webhook: adres yok";
                var hookCode = await PostJsonAsync(action.Target, new { rule = rule.Name, type, message, data = payload }, null, ct);
                return $"webhook: HTTP {hookCode?.ToString() ?? "hata"}";
            case "slack":
            case "teams":
                var kind = action.Type == "slack" ? "Slack" : "Teams";
                if (!string.IsNullOrWhiteSpace(action.Target))
                {
                    var code = await PostChatAsync(kind, action.Target, message, type, ct);
                    return $"{action.Type}: HTTP {code?.ToString() ?? "hata"}";
                }
                var targets = await db.Integrations.Where(i => i.TenantSlug == tenant && i.IsEnabled && i.Kind == kind).ToListAsync(ct);
                foreach (var t in targets) t.LastStatus = await PostChatAsync(kind, t.WebhookUrl, message, type, ct);
                return $"{action.Type}: {targets.Count} kanala gönderildi";
            default:
                return $"{action.Type}: bilinmeyen eylem";
        }
    }

    /// <summary>
    /// Tek teslimat denemesi. Dalga 12: teslimat satırı olay kimliği ve deneme sırasıyla
    /// yazılır; geçici hatada <see cref="WebhookRetry"/> sonraki deneme zamanını ayarlar.
    /// </summary>
    public async Task<WebhookDelivery> DeliverWebhookAsync(GovernanceDbContext db, Webhook hook, Guid eventId, string type, JsonElement? payload, CancellationToken ct,
        int attempt = 1, Guid? parentId = null, bool manual = false, string? by = null)
    {
        var started = DateTime.UtcNow;
        // KVKK m.9: Zapier gibi yurt dışı hedefe dayanak kaydı (sonradan silinmiş olabilir) yoksa veri gönderilmez.
        if (TransferGuard.HookProvider(hook.Url) is { } provider && !(await TransferGuard.AllowedAsync(db, hook.TenantSlug, ct)).Contains(provider))
        {
            hook.LastStatus = null;
            hook.LastDeliveredAt = DateTime.UtcNow;
            var blocked = new WebhookDelivery
            {
                TenantSlug = hook.TenantSlug, WebhookId = hook.Id, EventType = type, StatusCode = null, Error = "transfer_basis_required", DurationMs = 0,
                EventId = eventId, Attempt = attempt, ParentDeliveryId = parentId, Manual = manual, TriggeredByName = by,
            };
            db.WebhookDeliveries.Add(blocked);
            return blocked;
        }
        var body = JsonSerializer.Serialize(new { id = eventId, type, tenant = hook.TenantSlug, occurredAt = started, data = payload }, Json);
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hook.Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        int? status = null;
        string? error = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-HR360-Event", type);
            req.Headers.Add("X-HR360-Delivery", eventId.ToString());
            req.Headers.Add("X-HR360-Signature", signature);
            req.Headers.Add("X-HR360-Attempt", attempt.ToString(CultureInfo.InvariantCulture));
            // SSRF: İK ekranından açılan webhook'lar bağlantı anında iç ağ IP'sine gidemez (DNS rebinding dahil);
            // REST hook (kiracının kendi n8n'i) kiracının iç ağına gidebilir ama yerel/meta veri adreslerine ve
            // HR360 altyapı servislerine gidemez. Servisin kendi test alıcısı serbest.
            HttpClient client;
            if (WebhookTargetGuard.IsSelfInbox(hook.Url))
            {
                client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
            }
            else client = hook.Source == "rest-hook" ? WebhookTargetGuard.RestHookClient : WebhookTargetGuard.Client;
            using var res = await client.SendAsync(req, ct);
            status = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode) error = $"HTTP {status}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
        }
        hook.LastStatus = status;
        hook.LastDeliveredAt = DateTime.UtcNow;
        hook.FailureCount = error is null ? 0 : hook.FailureCount + 1;
        if (hook.FailureCount >= 20) hook.IsEnabled = false; // sürekli düşen uç otomatik kapanır
        var delivery = new WebhookDelivery
        {
            TenantSlug = hook.TenantSlug, WebhookId = hook.Id, EventType = type, StatusCode = status, Error = error,
            DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            EventId = eventId, Attempt = attempt, ParentDeliveryId = parentId, Manual = manual, TriggeredByName = by,
        };
        WebhookRetry.Schedule(delivery, hook, type);
        db.WebhookDeliveries.Add(delivery);
        return delivery;
    }

    public async Task<int?> PostChatAsync(string kind, string url, string text, string type, CancellationToken ct)
    {
        // Teams: eski Office 365 bağlayıcıları (MessageCard) kapatıldı; Workflows
        // (Power Automate) webhook'u Adaptive Card ekli "message" bekler.
        object body = kind == "Teams"
            ? Chat.ChatFormat.TeamsWebhookMessage(text, type)
            : new
            {
                text = $"*HR360* — {text}", blocks = new object[]
            {
                new { type = "section", text = new { type = "mrkdwn", text = $"*HR360* · {text}" } },
                new { type = "context", elements = new object[] { new { type = "mrkdwn", text = $"`{type}`" } } },
            }
            };
        return await PostJsonAsync(url, body, null, ct);
    }

    private async Task<int?> PostJsonAsync(string url, object body, string? signature, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var res = await client.PostAsJsonAsync(url, body, Json, ct);
            return (int)res.StatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogInformation("Gönderim başarısız ({Url}): {Message}", url, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Saatlik bakım: 30 günden eski olayları siler, süresi gelen saklama
/// politikalarını (KVKK) çalıştırır, ay başında kiracı faturalarını keser.
/// </summary>
public sealed class Housekeeping : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<Housekeeping> _log;
    public Housekeeping(IServiceProvider sp, ILogger<Housekeeping> log) { _sp = sp; _log = log; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HR360.Platform.LeaderGate.RunAsync("governance/Housekeeping", stoppingToken, LeaderLoopAsync);

    private async Task LeaderLoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
                var sql = scope.ServiceProvider.GetRequiredService<Sql>();
                await sql.ExecuteAsync("DELETE FROM governance_events WHERE \"OccurredAt\" < now() - interval '30 days'", ct);

                var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
                var due = await db.RetentionPolicies.Where(p => p.IsEnabled && (p.LastRunAt == null || p.LastRunAt < DateTime.UtcNow.AddHours(-24))).ToListAsync(ct);
                var details = new List<(DestructionLog? Log, Retention.RunResult Run)>();
                foreach (var p in due)
                {
                    var run = await Retention.RunDetailedAsync(sql, p, ct);
                    p.LastAffected = run.Affected;
                    p.LastRunAt = DateTime.UtcNow;
                    details.Add((Retention.Log(db, p.TenantSlug, p.Category, p.Action, p.LastAffected, p.RetentionMonths, "Periodic", "Sistem (periyodik imha)"), run));
                }
                // Özel alan değerleri: alan tanımındaki saklama süresi her turda uygulanır (politikadan bağımsız).
                foreach (var (tenant, n) in await CustomFields.PurgeAsync(sql, null, ct))
                    Retention.Log(db, tenant, "CustomFieldValues", "Delete", n, 0, "Periodic", "Sistem (özel alan saklama süresi)");
                await db.SaveChangesAsync(ct);
                foreach (var (log, run) in details) await Retention.SaveDetailsAsync(sql, log, run, ct);
                // Dalga 10: imha edilen kayıtların nesne deposu dosyaları (madde 57), başvuru süre hatırlatmaları
                // (madde 54), rıza yenileme hatırlatmaları (madde 55) ve süresi dolan veri paketleri.
                await RunSafelyAsync("Nesne deposu silme", () => StorageDeletions.ProcessAsync(sql, scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(), null, ct));
                await RunSafelyAsync("Başvuru hatırlatmaları", () => DataRequestReminders.RunAsync(sql, ct));
                await RunSafelyAsync("Rıza yenileme hatırlatmaları", () => ConsentCampaigns.AutoRemindAsync(sql, ct));
                await RunSafelyAsync("Veri paketi temizliği", () => DataRequestPackages.PurgeExpiredAsync(sql, ct));
                // İleri tarihli duyuruların yayım bildirimi (liste açılışında da tetiklenir).
                await Controllers.AnnouncementsController.PublishDueAsync(sql, scope.ServiceProvider.GetRequiredService<PeopleDirectory>(), null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Bakım turu hata verdi");
            }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }

    /// <summary>Bir alt işin hatası (ör. tablo henüz yok) diğer bakım işlerini durdurmaz.</summary>
    private async Task RunSafelyAsync(string name, Func<Task<int>> job)
    {
        try { await job(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Bakım işi hata verdi: {Job}", name); }
    }
}

/// <summary>KVKK saklama süresi uygulayıcısı (anonimleştirme/silme).</summary>
public static class Retention
{
    public static readonly Dictionary<string, (string Label, string[] Actions, int DefaultMonths)> Categories = new()
    {
        ["RejectedCandidates"] = ("Olumsuz sonuçlanan aday başvuruları", new[] { "Anonymize", "Delete" }, 12),
        ["TerminatedEmployees"] = ("Ayrılmış çalışanların kişisel verileri", new[] { "Anonymize" }, 120),
        ["AuditLog"] = ("Denetim kayıtları", new[] { "Delete" }, 24),
        ["Notifications"] = ("Bildirim geçmişi", new[] { "Delete" }, 6),
        ["AiUsage"] = ("Yapay zekâ kullanım kayıtları", new[] { "Delete" }, 12),
        ["ChatMessages"] = ("Sohbet botu mesaj kayıtları", new[] { "Delete" }, 6),
        // Dalga 5e: asistan konuşma bağlamı — en çok 30 gün (bot ayrıca 30 günü geçeni her turda siler).
        ["ChatContext"] = ("Sohbet asistanı konuşma bağlamı (en çok 30 gün)", new[] { "Delete" }, 1),
        ["WebhookDeliveries"] = ("Webhook gönderim kayıtları", new[] { "Delete" }, 3),
        // SGK ve vergi mevzuatı: ücret bordroları 10 yıl saklanır (5510 s. K. m.86, VUK m.253).
        ["Payslips"] = ("Bordro pusulaları (kapanmış dönemler)", new[] { "Delete" }, 120),
        ["DocumentRequests"] = ("Çalışan belge talepleri ve düzenlenen belgeler", new[] { "Delete" }, 24),
        // Dalga 5c: işyeri uyumu
        ["DisciplinaryCases"] = ("Kapatılmış disiplin vakaları (savunma, tutanak, karar)", new[] { "Delete" }, 24),
        ["EthicsReports"] = ("Kapatılmış etik/ihbar bildirimleri ve yazışmaları", new[] { "Delete" }, 24),
        ["Announcements"] = ("Süresi dolmuş duyurular ve okuma kayıtları", new[] { "Delete" }, 12),
        // Dalga 5d: özel alan değerleri. Süre ALAN BAZINDADIR (alan tanımındaki saklama süresi);
        // bakım turu politika kapalı olsa da çalıştırır (süre alan oluşturulurken zorunlu tutulur).
        ["CustomFieldValues"] = ("Ayrılmış çalışanların özel alan değerleri (süre alan bazında)", new[] { "Delete" }, 1),
    };

    /// <summary>Saklama kategorisi etiketlerinin İngilizcesi (arayüz dili İngilizceyken).</summary>
    public static readonly Dictionary<string, string> LabelsEn = new()
    {
        ["RejectedCandidates"] = "Unsuccessful candidate applications",
        ["TerminatedEmployees"] = "Personal data of former employees",
        ["AuditLog"] = "Audit records",
        ["Notifications"] = "Notification history",
        ["AiUsage"] = "AI usage records",
        ["ChatMessages"] = "Chat bot message records",
        ["ChatContext"] = "Chat assistant conversation context (max. 30 days)",
        ["WebhookDeliveries"] = "Webhook delivery records",
        ["Payslips"] = "Payslips (closed periods)",
        ["DocumentRequests"] = "Employee document requests and issued documents",
        ["DisciplinaryCases"] = "Closed disciplinary cases (defence, minutes, decision)",
        ["EthicsReports"] = "Closed ethics/whistleblowing reports and correspondence",
        ["Announcements"] = "Expired announcements and read receipts",
        ["CustomFieldValues"] = "Custom field values of former employees (retention per field)",
    };

    /// <summary>Kategori etiketi (tr/en); bilinmeyen kategoride anahtarın kendisi.</summary>
    public static string LabelOf(string category, bool en) =>
        en && LabelsEn.TryGetValue(category, out var e) ? e : Categories.TryGetValue(category, out var c) ? c.Label : category;

    public static string MethodOf(string action) => action == "Anonymize"
        ? "Geri döndürülemez anonimleştirme: kimlik ve iletişim alanları silinir, istatistik alanları kalır"
        : "Veritabanından kalıcı silme";

    /// <summary>İmha tutanağı satırı (Silme, Yok Etme veya Anonim Hale Getirme Yönetmeliği: kayıtlar en az 3 yıl saklanır).</summary>
    public static DestructionLog? Log(GovernanceDbContext db, string tenant, string category, string action, int affected, int months, string trigger, string actor)
    {
        if (affected == 0 && trigger == "Periodic") return null;
        var log = new DestructionLog
        {
            TenantSlug = tenant, Category = category, Action = action, Affected = affected, RetentionMonths = months,
            Trigger = trigger, Actor = actor, Method = MethodOf(action),
        };
        db.DestructionLogs.Add(log);
        return log;
    }

    /// <summary>Dalga 10: tutanağa tablo bazında döküm (SaveChanges'tan sonra; sütun yoksa sessizce atlanır).</summary>
    public static async Task SaveDetailsAsync(Sql sql, DestructionLog? log, RunResult run, CancellationToken ct)
    {
        if (log is null) return;
        try
        {
            await sql.ExecuteAsync("UPDATE governance_destruction_logs SET \"Details\" = $2::jsonb WHERE \"Id\" = $1", ct, log.Id,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    subjects = run.Plan.Subjects,
                    tables = run.Steps.Select(s => new { table = s.Table, label = s.Label, kind = s.Kind, rows = s.Rows, skipped = s.Skipped }),
                    storageQueued = run.Queued,
                    retained = run.Plan.Retained.Select(r => new { table = r.Table, reason = r.Reason }),
                }));
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42703") { }
    }

    public sealed record RunResult(int Affected, RetentionPlan Plan, List<StepResult> Steps, int Queued);

    public static async Task<RunResult> RunDetailedAsync(Sql sql, RetentionPolicy p, CancellationToken ct)
    {
        var plan = await RetentionPlans.BuildAsync(sql, p.TenantSlug, p.Category, p.Action, p.RetentionMonths, ct);
        var (steps, queued) = await RetentionPlans.ExecuteAsync(sql, plan, ct);
        return new RunResult(RetentionPlans.Affected(plan, steps), plan, steps, queued);
    }

    public static async Task<int> RunAsync(Sql sql, RetentionPolicy p, CancellationToken ct) => (await RunDetailedAsync(sql, p, ct)).Affected;

    /// <summary>
    /// Ayrılmış çalışan(lar)ın kimliğini geri döndürülemez biçimde siler: ad, e-posta, telefon, profil (adres,
    /// IBAN, TCKN, acil durum kişisi, doğum tarihi) ve diğer servislerdeki ad/serbest metin alanları
    /// (RetentionPlans.EmployeeSteps). İstatistik ve yasal saklama alanları kalır.
    /// </summary>
    public static async Task<int> AnonymizeEmployeesAsync(Sql sql, string tenant, Guid? employeeId, int months, CancellationToken ct) =>
        (await AnonymizeEmployeesDetailedAsync(sql, tenant, employeeId, months, ct)).Affected;

    public static async Task<RunResult> AnonymizeEmployeesDetailedAsync(Sql sql, string tenant, Guid? employeeId, int months, CancellationToken ct)
    {
        var plan = await RetentionPlans.BuildAsync(sql, tenant, "TerminatedEmployees", "Anonymize", months, ct, employeeId);
        var (steps, queued) = await RetentionPlans.ExecuteAsync(sql, plan, ct);
        return new RunResult(plan.Subjects, plan, steps, queued);
    }
}
