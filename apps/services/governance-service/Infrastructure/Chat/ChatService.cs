using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Models;
using GovernanceService.Tenancy;

namespace GovernanceService.Infrastructure.Chat;

/// <summary>Bekleyen bir onay adımı (mesaj içeriği için).</summary>
public sealed record PendingApproval(Guid WorkflowId, Guid StepId, string Type, string? Subject, string? Requester, DateTime? SlaDueAt, string? Details, string? Payload = null);

public enum ChatForm { None, Leave, Expense }

/// <summary>B6: fişten okunan masraf önerisi (kullanıcı onaylayınca taslak beyan olur).</summary>
public sealed record ExpenseDraft(Guid PendingId, decimal? Amount, DateOnly? Date, string Category);

/// <summary>Komut yanıtı: metin, (varsa) düğmeli onay listesi, bağlantı ya da form.</summary>
public sealed record ChatReply(string Text, IReadOnlyList<PendingApproval> Approvals)
{
    public bool En { get; init; }
    public string? Link { get; init; }
    public ChatForm Form { get; init; }
    public IReadOnlyList<string>? FormLines { get; init; }
    /// <summary>Dalga 5e: eylem / hızlı yanıt / "Panelde aç" düğmeleri.</summary>
    public IReadOnlyList<ChatButton>? Buttons { get; init; }
    public ExpenseDraft? Expense { get; init; }
    /// <summary>Düğmeye basılınca eski kartın yerine bu yanıt konur (BG10: eski düğmeler kalkar).</summary>
    public bool Replace { get; init; }

    public static ChatReply Of(string text, bool en, params ChatButton[] buttons) =>
        new(text, Array.Empty<PendingApproval>()) { En = en, Buttons = buttons.Length == 0 ? null : buttons };
}

/// <summary>
/// Slack ve Teams için ortak iş mantığı: onaycıya düğmeli mesaj, karar verme
/// (workflow-service iç ucu üzerinden), talep sahibine sonuç bildirimi, komutlar.
/// Platforma özgü biçim <see cref="ChatFormat"/>'tadır.
/// </summary>
public sealed class ChatService
{
    private readonly Sql _sql;
    private readonly SlackApi _slack;
    private readonly TeamsApi _teams;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ChatService> _log;
    private static readonly string WorkflowBase = (EnvVar.Or("WORKFLOW_SERVICE_URL", "http://workflow-service:8080")).TrimEnd('/');
    public static readonly string PublicOrigin = (EnvVar.Or("PUBLIC_ORIGIN", "http://localhost")).TrimEnd('/');
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly PeopleDirectory _people;

    public ChatService(Sql sql, SlackApi slack, TeamsApi teams, IHttpClientFactory http, ILogger<ChatService> log, PeopleDirectory people)
    {
        _sql = sql; _slack = slack; _teams = teams; _http = http; _log = log; _people = people;
        Features = new ChatFeatures(this, sql, slack, teams, http, people, log);
    }

    /// <summary>Dalga 5e özellikleri (Mattermost/Rocket.Chat, düğme eylemleri, zamanlanmış işler...).</summary>
    public ChatFeatures Features { get; }

    // ------------------------------------------------------------------ güvenlik ve veri en aza indirme

    /// <summary>
    /// Sohbet hesabına veri gönderilebilir mi: çalışan kaydı eşleşmiş ve (uygulama istiyorsa)
    /// kişi HR360'a giriş yapıp bağlamayı onaylamış olmalı.
    /// </summary>
    public static bool Trusted(ChatApp app, ChatIdentity who) =>
        who.EmployeeId is not null && (who.VerifiedAt is not null || !app.RequireVerifiedIdentity);

    public static string HashCode(string code) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

    /// <summary>Tek kullanımlık bağlama bağlantısı üretir (yalnızca özeti saklanır).</summary>
    public async Task<string> LinkUrlAsync(GovernanceDbContext db, ChatIdentity who, TimeSpan validity, CancellationToken ct)
    {
        var code = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        who.LinkCodeHash = HashCode(code);
        who.LinkCodeExpiresAt = DateTime.UtcNow + validity;
        await db.SaveChangesAsync(ct);
        return $"{PublicOrigin}/panel/sohbet-bagla?kod={code}";
    }

    public static string LinkPrompt(string url, bool forApproval = false, bool en = false) => en
        ? (forApproval ? "A request is awaiting your approval. " : "")
          + "🔒 For your security, link this chat account to your HR360 account once. Open the link, sign in to HR360 and confirm: " + url + " (valid for 24 hours)"
        : (forApproval ? "Onayınızı bekleyen bir talep var. " : "")
          + "🔒 Güvenliğiniz için bu sohbet hesabını HR360 hesabınıza bir kez bağlamanız gerekiyor. Bağlantıyı açıp HR360'a giriş yapın ve onaylayın: " + url + " (24 saat geçerli)";

    /// <summary>Kişinin dil tercihi (Profil › EN/TR düğmesi, bildirim tercihiyle aynı). Kayıt yoksa Türkçe.</summary>
    public async Task<bool> EnAsync(string tenant, Guid? employeeId, CancellationToken ct) =>
        employeeId is not null && await _sql.ScalarAsync(
            "SELECT \"Language\" FROM notification_preferences WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, tenant, employeeId.Value) as string == "en";

    /// <summary>Ayrılan ya da silinen çalışanın sohbet eşleşmesi kaldırılır (her istekte denetlenir).</summary>
    public async Task<bool> EnsureActiveAsync(GovernanceDbContext db, ChatIdentity who, CancellationToken ct)
    {
        if (who.EmployeeId is null) return false;
        var status = await _sql.ScalarAsync("SELECT \"Status\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, who.TenantSlug, who.EmployeeId.Value) as string;
        if (status is not null && status != "Terminated") return true;
        who.EmployeeId = null; who.VerifiedAt = null; who.LinkCodeHash = null;
        await db.SaveChangesAsync(ct);
        return false;
    }

    /// <summary>"Ayşe Yılmaz" → "Ayşe Y." (Minimal mesaj ayrıntısı).</summary>
    public static string? ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? parts[0] : $"{string.Join(' ', parts[..^1])} {char.ToUpper(parts[^1][0], Tr)}.";
    }

    /// <summary>KVKK veri en aza indirme: yurt dışındaki sohbet hizmetine yalnızca gerekli bilgi gider.</summary>
    public static PendingApproval Shape(ChatApp app, PendingApproval p, bool en = false)
    {
        p = p with { Details = Details(p.Payload, en) ?? (en ? null : p.Details) };
        return app.MessageDetail == "Standard" ? p : p with { Requester = ShortName(p.Requester), Subject = null };
    }

    public static string TypeLabel(string? type, bool en = false) => en ? type switch
    {
        "LeaveRequest" => "leave", "ExpenseClaim" => "expense", "PositionChange" => "position change",
        "AssetRequest" => "asset", "Overtime" => "overtime", "DocumentRequest" => "document", "Travel" => "travel", "OfferApproval" => "offer", _ => "approval",
    } : type switch
    {
        "LeaveRequest" => "izin", "ExpenseClaim" => "masraf", "PositionChange" => "pozisyon değişikliği",
        "AssetRequest" => "zimmet", "Overtime" => "fazla mesai", "DocumentRequest" => "belge", "Travel" => "seyahat", "OfferApproval" => "teklif", _ => "onay",
    };

    public static string LeaveTypeLabel(string t, bool en) => HrAssistant.LeaveLabel(t, en);

    public static string LeaveLabel(string type) => type switch
    {
        "Annual" => "Yıllık", "Sick" => "Hastalık", "Unpaid" => "Ücretsiz", "Maternity" => "Doğum", "Paternity" => "Babalık",
        "Marriage" => "Evlilik", "Bereavement" => "Vefat", _ => type,
    };

    public static string WorkflowUrl(Guid id) => $"{PublicOrigin}/panel/onaylar/{id}";

    // ------------------------------------------------------------------ olaylar

    public async Task OnEventAsync(GovernanceDbContext db, string tenant, string type, JsonElement? payload, CancellationToken ct)
    {
        if (type is not ("workflow.submitted" or "workflow.approved" or "workflow.rejected")) return;
        var apps = await db.ChatApps.Where(a => a.TenantSlug == tenant && a.IsEnabled).ToListAsync(ct);
        // KVKK m.9: dayanak kaydı olmayan yurt dışı hizmete veri gönderilmez.
        var allowed = await TransferGuard.AllowedAsync(db, tenant, ct);
        apps = apps.Where(a => ChatHosts.Allowed(a, allowed)).ToList();
        if (apps.Count == 0) return;
        if (!Guid.TryParse(EventHub.Field(payload, "WorkflowRequestId"), out var wfId)) return;

        if (type == "workflow.submitted")
        {
            if (!Guid.TryParse(EventHub.Field(payload, "ApproverEmployeeId"), out var approverId)) return;
            var pending = await PendingStepAsync(tenant, wfId, approverId, ct);
            if (pending is null) return;
            // Sıra bir sonraki adıma geçtiyse önceki adımın mesajındaki düğmeler kaldırılır.
            await CloseMessagesAsync(db, apps, tenant, wfId, keepStep: pending.StepId,
                en => en ? "This step was approved; the request moved to the next approver." : "Bu adım onaylandı; talep bir sonraki onaycıya geçti.", ct);
            foreach (var app in apps.Where(a => a.NotifyApprovals))
            {
                try { await SendApprovalAsync(db, app, approverId, EventHub.Field(payload, "ApproverEmail"), pending, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await FailAsync(db, app, ex, ct);
                    Enqueue(db, app, "approval", new { approverId, approverEmail = EventHub.Field(payload, "ApproverEmail"), wfId }, ex);
                }
            }
        }
        else
        {
            var approved = type == "workflow.approved";
            Guid.TryParse(EventHub.Field(payload, "DecidedByEmployeeId"), out var decidedBy);
            var who = decidedBy == Guid.Empty ? null : await NameOfAsync(tenant, decidedBy, ct);
            await CloseMessagesAsync(db, apps, tenant, wfId, null,
                en => $"{(approved ? (en ? "✅ Approved" : "✅ Onaylandı") : (en ? "⛔ Rejected" : "⛔ Reddedildi"))}{(who is null ? "" : " — " + who)}", ct);
            if (!Guid.TryParse(EventHub.Field(payload, "RequesterEmployeeId"), out var requesterId)) return;
            var subject = EventHub.Field(payload, "Subject");
            var rEn = await EnAsync(tenant, requesterId, ct);
            var label = TypeLabel(EventHub.Field(payload, "WorkflowType"), rEn);
            var comment = EventHub.Field(payload, "Comment");
            // Karar notu (gerekçe) sohbete yazılmaz; yalnızca HR360'ta görülür (KVKK veri en aza indirme).
            var hasNote = !string.IsNullOrWhiteSpace(comment) && !System.Text.RegularExpressions.Regex.IsMatch(comment.Trim(), @"^\((Slack|Teams) üzerinden\)$");
            var text = rEn
                ? $"{(approved ? "✅" : "⛔")} Your {label} request was {(approved ? "approved" : "rejected")}: {subject}"
                  + (who is null ? "" : $"\nDecision by: {who}") + (hasNote ? "\nThere is a note on the decision; open it in HR360." : "")
                : $"{(approved ? "✅" : "⛔")} {char.ToUpper(label[0], Tr)}{label[1..]} talebiniz {(approved ? "onaylandı" : "reddedildi")}: {subject}"
                  + (who is null ? "" : $"\nKarar: {who}") + (hasNote ? "\nKarar notu var; HR360'ta açarak görebilirsiniz." : "");
            foreach (var app in apps.Where(a => a.NotifyRequesters))
            {
                // BG6: sonuç bildirimi kritik değildir; kişinin sessiz saatinde ertelenir.
                try { await Features.SendAsync(db, app, requesterId, ChatReply.Of(text, rEn) with { Link = WorkflowUrl(wfId) }, "workflow.result", false, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await FailAsync(db, app, ex, ct);
                    Enqueue(db, app, "text", new { employeeId = requesterId, text, link = WorkflowUrl(wfId) }, ex);
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ yeniden deneme kuyruğu

    /// <summary>Deneme aralıkları (taban × 1, 5, 15, 60, 180). Taban varsayılan 60 sn; testlerde kısaltılır.</summary>
    public static readonly int RetryBaseSeconds = int.TryParse(EnvVar.Or("CHAT_RETRY_BASE_SECONDS", "60"), out var b) && b > 0 ? b : 60;
    private static readonly int[] RetrySteps = { 1, 5, 15, 60, 180 };

    private static void Enqueue(GovernanceDbContext db, ChatApp app, string kind, object payload, Exception ex) =>
        db.ChatOutbox.Add(new ChatOutbox
        {
            TenantSlug = app.TenantSlug, AppId = app.Id, Kind = kind, Payload = JsonSerializer.Serialize(payload),
            Attempts = 1, NextAttemptAt = DateTime.UtcNow.AddSeconds(RetryBaseSeconds * RetrySteps[0]), LastError = Trim(ex.Message),
        });

    private static string Trim(string s) => s.Length > 500 ? s[..500] : s;

    /// <summary>Vadesi gelen kuyruk kayıtlarını dener; başarılı olanı siler, olmayanı erteler, beşinci denemeden sonra bırakır.</summary>
    public async Task<int> ProcessOutboxAsync(GovernanceDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        ChatMetrics.QueueDepth.Set(await db.ChatOutbox.IgnoreQueryFilters().CountAsync(ct));
        var due = await db.ChatOutbox.IgnoreQueryFilters().Where(o => o.NextAttemptAt <= now).OrderBy(o => o.NextAttemptAt).Take(50).ToListAsync(ct);
        var sent = 0;
        foreach (var o in due)
        {
            var app = await db.ChatApps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == o.AppId && a.IsEnabled, ct);
            if (app is null || !ChatHosts.Allowed(app, await TransferGuard.AllowedAsync(db, o.TenantSlug, ct)))
            {
                db.ChatOutbox.Remove(o);
                continue;
            }
            try
            {
                var p = JsonDocument.Parse(o.Payload).RootElement;
                if (o.Kind == "approval")
                {
                    var approverId = p.GetProperty("approverId").GetGuid();
                    var pending = await PendingStepAsync(o.TenantSlug, p.GetProperty("wfId").GetGuid(), approverId, ct);
                    // Talep bu arada karara bağlandıysa gönderilecek bir şey kalmadı.
                    if (pending is not null)
                        await SendApprovalAsync(db, app, approverId, p.TryGetProperty("approverEmail", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null, pending, ct);
                }
                else if (o.Kind == "card")
                {
                    // BG6: sessiz saatte ertelenen mesaj (vakti gelince tercih yeniden denetlenir).
                    var card = JsonSerializer.Deserialize<ChatFeatures.OutboxCard>(o.Payload)!;
                    var outcome = await Features.SendAsync(db, app, card.EmployeeId, ChatFeatures.Deserialize(JsonDocument.Parse(card.Reply.ToJsonString()).RootElement),
                        card.TemplateCode, o.Attempts > 0, ct);
                    _ = outcome;
                }
                else if (o.Kind == "text")
                {
                    await SendTextToEmployeeAsync(db, app, p.GetProperty("employeeId").GetGuid(), p.GetProperty("text").GetString() ?? "",
                        p.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null, ct);
                }
                db.ChatOutbox.Remove(o);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                o.LastError = Trim(ex.Message);
                if (o.Attempts >= RetrySteps.Length)
                {
                    _log.LogWarning("Sohbet mesajı {Attempts} denemede gönderilemedi, bırakıldı: {Message}", o.Attempts, ex.Message);
                    app.LastError = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {o.Attempts} denemede gönderilemedi: {ex.Message}";
                    db.ChatOutbox.Remove(o);
                }
                else
                {
                    o.NextAttemptAt = DateTime.UtcNow.AddSeconds(RetryBaseSeconds * RetrySteps[o.Attempts]);
                    o.Attempts++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private async Task FailAsync(GovernanceDbContext db, ChatApp app, Exception ex, CancellationToken ct)
    {
        _log.LogWarning("{Platform} uygulaması ({App}) gönderim hatası: {Message}", app.Platform, app.Id, ex.Message);
        app.LastError = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {ex.Message}";
        await Task.CompletedTask;
    }

    public async Task<PendingApproval?> PendingStepAsync(string tenant, Guid wfId, Guid approverId, CancellationToken ct) =>
        (await _sql.QueryAsync("""
            SELECT w."Id", s."Id", w."Type", w."Subject", e."FirstName" || ' ' || e."LastName", w."SlaDueAt", w."Payload"
            FROM workflow_requests w
            JOIN workflow_approval_steps s ON s."WorkflowRequestId" = w."Id" AND s."Decision" = 'Pending'
            LEFT JOIN employee_employees e ON e."Id" = w."RequesterEmployeeId"
            WHERE w."TenantSlug" = $1 AND w."Id" = $2 AND w."Status" = 'Pending'
              AND (s."ApproverEmployeeId" = $3 OR s."DelegatedToEmployeeId" = $3)
              AND NOT EXISTS (SELECT 1 FROM workflow_approval_steps p WHERE p."WorkflowRequestId" = w."Id" AND p."Order" < s."Order" AND p."Decision" = 'Pending')
            ORDER BY s."Order" LIMIT 1
            """, MapPending, ct, tenant, wfId, approverId)).FirstOrDefault();

    public async Task<List<PendingApproval>> MyPendingAsync(string tenant, Guid employeeId, int limit, CancellationToken ct) =>
        await _sql.QueryAsync("""
            SELECT w."Id", s."Id", w."Type", w."Subject", e."FirstName" || ' ' || e."LastName", w."SlaDueAt", w."Payload"
            FROM workflow_requests w
            JOIN workflow_approval_steps s ON s."WorkflowRequestId" = w."Id" AND s."Decision" = 'Pending'
            LEFT JOIN employee_employees e ON e."Id" = w."RequesterEmployeeId"
            WHERE w."TenantSlug" = $1 AND w."Status" = 'Pending' AND w."RequesterEmployeeId" <> $2
              AND (s."ApproverEmployeeId" = $2 OR s."DelegatedToEmployeeId" = $2)
              AND NOT EXISTS (SELECT 1 FROM workflow_approval_steps p WHERE p."WorkflowRequestId" = w."Id" AND p."Order" < s."Order" AND p."Decision" = 'Pending')
            ORDER BY w."CreatedAt" DESC LIMIT $3
            """, MapPending, ct, tenant, employeeId, limit);

    public async Task<long> MyPendingCountAsync(string tenant, Guid employeeId, CancellationToken ct) =>
        Convert.ToInt64(await _sql.ScalarAsync("""
            SELECT count(*) FROM workflow_requests w
            JOIN workflow_approval_steps s ON s."WorkflowRequestId" = w."Id" AND s."Decision" = 'Pending'
            WHERE w."TenantSlug" = $1 AND w."Status" = 'Pending' AND w."RequesterEmployeeId" <> $2
              AND (s."ApproverEmployeeId" = $2 OR s."DelegatedToEmployeeId" = $2)
              AND NOT EXISTS (SELECT 1 FROM workflow_approval_steps p WHERE p."WorkflowRequestId" = w."Id" AND p."Order" < s."Order" AND p."Decision" = 'Pending')
            """, ct, tenant, employeeId));

    private static PendingApproval MapPending(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Str(3), r.Str(4), r.Ts(5), Details(r.Str(6)), r.Str(6));

    /// <summary>Talep yükünden kısa ayrıntı (izin tarihleri, masraf tutarı). Bilinmeyen yapı sessizce atlanır.</summary>
    private static string? Details(string? payload, bool en = false)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            var p = JsonDocument.Parse(payload).RootElement;
            string? F(params string[] names)
            {
                foreach (var n in names)
                    foreach (var prop in p.EnumerateObject())
                        if (string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            return prop.Value.ToString();
                return null;
            }
            var start = F("startDate"); var end = F("endDate"); var days = F("days", "totalDays", "workingDays");
            var amount = F("amount", "totalAmount"); var currency = F("currency");
            var parts = new List<string>();
            if (start is not null && end is not null)
                parts.Add(DateOnly.TryParse(start[..Math.Min(10, start.Length)], CultureInfo.InvariantCulture, out var s) && DateOnly.TryParse(end[..Math.Min(10, end.Length)], CultureInfo.InvariantCulture, out var e)
                    ? $"{s.ToString("d MMM", en ? CultureInfo.GetCultureInfo("en-GB") : Tr)} – {e.ToString("d MMM yyyy", en ? CultureInfo.GetCultureInfo("en-GB") : Tr)}" : $"{start} – {end}");
            if (days is not null) parts.Add(en ? $"{days} day(s)" : $"{days} gün");
            if (amount is not null) parts.Add($"{amount} {currency ?? "TL"}");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
        catch (JsonException) { return null; }
    }

    private async Task<string?> NameOfAsync(string tenant, Guid employeeId, CancellationToken ct) =>
        await _sql.ScalarAsync("SELECT \"FirstName\" || ' ' || \"LastName\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, tenant, employeeId) as string;

    public async Task<Guid?> EmployeeIdByEmailAsync(string tenant, string email, CancellationToken ct) => (await EmployeeByEmailAsync(tenant, email, ct))?.Id;

    private async Task<(Guid Id, string Name)?> EmployeeByEmailAsync(string tenant, string email, CancellationToken ct)
    {
        var r = (await _sql.QueryAsync("""
            SELECT "Id", "FirstName" || ' ' || "LastName" FROM employee_employees
            WHERE "TenantSlug" = $1 AND lower("Email") = lower($2) AND "Status" <> 'Terminated' LIMIT 1
            """, r => (r.GetGuid(0), r.GetString(1)), ct, tenant, email.Trim())).FirstOrDefault();
        return r.Item1 == Guid.Empty ? null : r;
    }

    // ------------------------------------------------------------------ kimlik eşleştirme

    /// <summary>Slack kullanıcısını (e-postasıyla) çalışana bağlar; DM kanalını açar.</summary>
    public async Task<ChatIdentity> EnsureSlackIdentityAsync(GovernanceDbContext db, ChatApp app, string slackUserId, CancellationToken ct)
    {
        var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.ExternalUserId == slackUserId, ct);
        var token = SecretBox.Unprotect(app.SlackBotTokenEnc)!;
        if (id is null)
        {
            var (email, name) = await _slack.UserInfoAsync(token, slackUserId, ct);
            id = new ChatIdentity { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = "Slack", ExternalUserId = slackUserId, Email = email, DisplayName = name };
            db.ChatIdentities.Add(id);
        }
        if (id.EmployeeId is null && id.Email is not null)
            id.EmployeeId = (await EmployeeByEmailAsync(app.TenantSlug, id.Email, ct))?.Id;
        id.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>Çalışanın Slack hesabını e-postayla bulur (gerekirse) ve DM kanalını döndürür.</summary>
    public async Task<ChatIdentity?> SlackIdentityForEmployeeAsync(GovernanceDbContext db, ChatApp app, Guid employeeId, string? email, CancellationToken ct)
    {
        var token = SecretBox.Unprotect(app.SlackBotTokenEnc)!;
        var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == employeeId, ct);
        if (id is null)
        {
            email ??= await _sql.ScalarAsync("SELECT \"Email\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, app.TenantSlug, employeeId) as string;
            if (string.IsNullOrWhiteSpace(email)) return null;
            var userId = await _slack.LookupByEmailAsync(token, email, ct);
            if (userId is null) return null;
            id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.ExternalUserId == userId, ct);
            if (id is null)
            {
                id = new ChatIdentity { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = "Slack", ExternalUserId = userId, Email = email };
                db.ChatIdentities.Add(id);
            }
            id.EmployeeId = employeeId;
        }
        id.ConversationId ??= await _slack.OpenDmAsync(token, id.ExternalUserId, ct);
        return id;
    }

    /// <summary>
    /// Teams kullanıcısını kaydeder. Teams'te bot bir kişiye ancak o kişi botu
    /// ekledikten (ya da yazdıktan) sonra mesaj atabilir; konuşma bilgisi burada saklanır.
    /// </summary>
    public async Task<ChatIdentity> EnsureTeamsIdentityAsync(GovernanceDbContext db, ChatApp app, string userId, string? aadObjectId,
        string serviceUrl, string conversationId, bool personal, CancellationToken ct)
    {
        var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.ExternalUserId == userId, ct);
        if (id is null)
        {
            id = new ChatIdentity { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = "Teams", ExternalUserId = userId };
            db.ChatIdentities.Add(id);
        }
        if (personal) { id.ConversationId = conversationId; id.ServiceUrl = serviceUrl; }
        if (id.Email is null)
        {
            try
            {
                var token = await TeamsTokenAsync(app, ct);
                var (email, name, _) = await _teams.MemberAsync(token, serviceUrl, conversationId, userId, ct);
                id.Email = email; id.DisplayName = name;
            }
            catch (ChatApiException ex) { _log.LogInformation("Teams üye bilgisi alınamadı: {Message}", ex.Message); }
        }
        if (id.EmployeeId is null && id.Email is not null)
            id.EmployeeId = (await EmployeeByEmailAsync(app.TenantSlug, id.Email, ct))?.Id;
        id.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return id;
    }

    public Task<string> TeamsTokenAsync(ChatApp app, CancellationToken ct) =>
        _teams.TokenAsync(app.TeamsAppId!, SecretBox.Unprotect(app.TeamsAppPasswordEnc)!, app.TeamsAzureTenantId!, ct);

    // ------------------------------------------------------------------ gönderim

    private async Task SendApprovalAsync(GovernanceDbContext db, ChatApp app, Guid approverId, string? approverEmail, PendingApproval p, CancellationToken ct)
    {
        if (await db.ChatMessages.AnyAsync(m => m.AppId == app.Id && m.StepId == p.StepId && m.RecipientEmployeeId == approverId, ct)) return;
        var en = await EnAsync(app.TenantSlug, approverId, ct);
        p = await Features.EnrichAsync(app.TenantSlug, Shape(app, p, en), en, ct);
        if (app.Platform is "Mattermost" or "RocketChat")
        {
            await Features.SendApprovalGenericAsync(db, app, approverId, p, en, ct);
            return;
        }
        if (app.Platform == "Slack")
        {
            var id = await SlackIdentityForEmployeeAsync(db, app, approverId, approverEmail, ct);
            if (id?.ConversationId is null) return;
            if (!Trusted(app, id))
            {
                // Doğrulanmamış hesaba talep içeriği gönderilmez; yalnızca bağlama bağlantısı.
                var prompt = LinkPrompt(await LinkUrlAsync(db, id, TimeSpan.FromHours(24), ct), forApproval: true, en);
                await _slack.PostAsync(SecretBox.Unprotect(app.SlackBotTokenEnc)!, id.ConversationId, prompt, ChatFormat.SlackText(prompt, null, en), ct);
                return;
            }
            var ts = await _slack.PostAsync(SecretBox.Unprotect(app.SlackBotTokenEnc)!, id.ConversationId,
                ChatFormat.ApprovalFallback(p, en), ChatFormat.SlackApproval(p, null, en), ct);
            db.ChatMessages.Add(new ChatMessage { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = "Slack", WorkflowRequestId = p.WorkflowId,
                StepId = p.StepId, RecipientEmployeeId = approverId, ConversationId = id.ConversationId, MessageId = ts, Subject = p.Subject });
        }
        else
        {
            var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == approverId && i.ConversationId != null, ct);
            if (id is null) return; // kişi botu henüz eklemedi
            var token = await TeamsTokenAsync(app, ct);
            if (!Trusted(app, id))
            {
                var prompt = LinkPrompt(await LinkUrlAsync(db, id, TimeSpan.FromHours(24), ct), forApproval: true, en);
                await _teams.PostActivityAsync(token, id.ServiceUrl!, id.ConversationId!, ChatFormat.TeamsActivity(ChatFormat.TeamsTextCard(prompt, null, en), prompt), ct);
                return;
            }
            var activityId = await _teams.PostActivityAsync(token, id.ServiceUrl!, id.ConversationId!, ChatFormat.TeamsActivity(ChatFormat.TeamsApprovalCard(p, null, en), ChatFormat.ApprovalFallback(p, en)), ct);
            db.ChatMessages.Add(new ChatMessage { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = "Teams", WorkflowRequestId = p.WorkflowId,
                StepId = p.StepId, RecipientEmployeeId = approverId, ConversationId = id.ConversationId!, ServiceUrl = id.ServiceUrl, MessageId = activityId ?? "", Subject = p.Subject });
        }
        app.LastActivityAt = DateTime.UtcNow;
        app.LastError = null;
    }

    /// <summary>Çalışana düz metin mesajı (sonuç bildirimi, deneme mesajı). Eşleşme yoksa false.</summary>
    public async Task<bool> SendTextToEmployeeAsync(GovernanceDbContext db, ChatApp app, Guid employeeId, string text, string? link, CancellationToken ct)
    {
        if (app.Platform is "Mattermost" or "RocketChat")
            return await Features.DeliverAsync(db, app, employeeId, ChatReply.Of(text, await EnAsync(app.TenantSlug, employeeId, ct)) with { Link = link }, ct);
        if (app.Platform == "Slack")
        {
            var id = await SlackIdentityForEmployeeAsync(db, app, employeeId, null, ct);
            if (id?.ConversationId is null || !Trusted(app, id)) return false;
            await _slack.PostAsync(SecretBox.Unprotect(app.SlackBotTokenEnc)!, id.ConversationId, text, ChatFormat.SlackText(text, link, await EnAsync(app.TenantSlug, employeeId, ct)), ct);
        }
        else
        {
            var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == employeeId && i.ConversationId != null, ct);
            if (id is null || !Trusted(app, id)) return false;
            var token = await TeamsTokenAsync(app, ct);
            await _teams.PostActivityAsync(token, id.ServiceUrl!, id.ConversationId!, ChatFormat.TeamsActivity(ChatFormat.TeamsTextCard(text, link, await EnAsync(app.TenantSlug, employeeId, ct)), text), ct);
        }
        app.LastActivityAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>Açık onay mesajlarının düğmelerini kaldırıp durum satırı ekler.</summary>
    private async Task CloseMessagesAsync(GovernanceDbContext db, List<ChatApp> apps, string tenant, Guid wfId, Guid? keepStep, Func<bool, string> status, CancellationToken ct)
    {
        var open = await db.ChatMessages.Where(m => m.TenantSlug == tenant && m.WorkflowRequestId == wfId && m.State == "Open"
            && (keepStep == null || m.StepId != keepStep)).ToListAsync(ct);
        foreach (var m in open)
        {
            var app = apps.FirstOrDefault(a => a.Id == m.AppId);
            m.State = keepStep is null ? "Decided" : "Closed";
            m.UpdatedAt = DateTime.UtcNow;
            if (app is null) continue;
            try { await UpdateMessageAsync(app, m, status, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await FailAsync(db, app, ex, ct); }
        }
    }

    public async Task UpdateMessageAsync(ChatApp app, ChatMessage m, Func<bool, string> statusOf, CancellationToken ct)
    {
        var en = await EnAsync(app.TenantSlug, m.RecipientEmployeeId, ct);
        var status = statusOf(en);
        var p = new PendingApproval(m.WorkflowRequestId, m.StepId, "", m.Subject, null, null, null);
        var full = (await _sql.QueryAsync("""
            SELECT w."Id", $3::uuid, w."Type", w."Subject", e."FirstName" || ' ' || e."LastName", w."SlaDueAt", w."Payload"
            FROM workflow_requests w LEFT JOIN employee_employees e ON e."Id" = w."RequesterEmployeeId"
            WHERE w."TenantSlug" = $1 AND w."Id" = $2
            """, MapPending, ct, app.TenantSlug, m.WorkflowRequestId, m.StepId)).FirstOrDefault() ?? p;
        full = await Features.EnrichAsync(app.TenantSlug, Shape(app, full, en), en, ct);
        if (m.Platform is "Mattermost" or "RocketChat")
            await Features.UpdateGenericAsync(app, m, ChatFormat.StatusFallback(status, full), ct);
        else if (m.Platform == "Slack")
            await _slack.UpdateAsync(SecretBox.Unprotect(app.SlackBotTokenEnc)!, m.ConversationId, m.MessageId, ChatFormat.StatusFallback(status, full), ChatFormat.SlackApproval(full, status, en), ct);
        else if (!string.IsNullOrEmpty(m.MessageId) && m.ServiceUrl is not null)
        {
            var token = await TeamsTokenAsync(app, ct);
            var activity = ChatFormat.TeamsActivity(ChatFormat.TeamsApprovalCard(full, status, en), ChatFormat.StatusFallback(status, full));
            activity["id"] = m.MessageId;
            await _teams.UpdateActivityAsync(token, m.ServiceUrl, m.ConversationId, m.MessageId, activity, ct);
        }
    }

    // ------------------------------------------------------------------ karar

    public sealed record DecisionResult(bool Ok, string Message, string? Status)
    {
        /// <summary>BG13: karar ek doğrulama bekliyor (yanıt olarak gösterilecek bağlantı kartı).</summary>
        public ChatReply? StepUp { get; init; }
    }

    /// <summary>Sohbetten gelen "Onayla/Reddet". Yetki kontrolü workflow-service'tedir.</summary>
    public async Task<DecisionResult> DecideAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, Guid wfId, Guid stepId, bool approve, CancellationToken ct, string? comment = null,
        bool stepUpDone = false)
    {
        if (!await EnsureActiveAsync(db, who, ct))
            return new(false, $"Sohbet hesabınız ({who.Email ?? "e-posta yok"}) HR360'taki etkin bir çalışan kaydıyla eşleşmedi. İK'dan e-posta adresinizi kontrol etmesini isteyin.", null);
        var en = await EnAsync(app.TenantSlug, who.EmployeeId, ct);
        if (!Trusted(app, who))
            return new(false, LinkPrompt(await LinkUrlAsync(db, who, TimeSpan.FromHours(24), ct), en: en), null);
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token)) return new(false, "Sunucuda INTERNAL_SERVICE_TOKEN tanımlı değil; sohbetten onay kapalı.", null);
        // BG13: ücretle ilgili onaylar sohbetten ancak HR360'ta ek doğrulamayla verilir.
        if (!stepUpDone && approve && await Features.SensitiveWorkflowAsync(app.TenantSlug, wfId, ct))
        {
            var card = await Features.StepUpAsync(app, who.EmployeeId!.Value, "decide", new { wf = wfId, step = stepId, approve },
                en ? "Approve a pay-related request" : "Ücretle ilgili bir talebi onaylama", en, ct);
            return new(false, card.Text, null) { StepUp = card };
        }

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{WorkflowBase}/api/internal/workflows/{wfId}/steps/{stepId}/decide")
        {
            Content = JsonContent.Create(new { tenantSlug = app.TenantSlug, actorEmployeeId = who.EmployeeId, decision = approve ? "Approved" : "Rejected", channel = app.Platform,
                comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(500, comment.Trim().Length)] }),
        };
        req.Headers.Add("X-Internal-Token", token);
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        string? Msg() { try { return JsonDocument.Parse(body).RootElement.GetProperty("message").GetString(); } catch { return null; } }
        string? Status() { try { return JsonDocument.Parse(body).RootElement.GetProperty("status").GetString(); } catch { return null; } }

        if (res.IsSuccessStatusCode)
        {
            string StatusOf(bool e) => (approve ? (e ? "✅ You approved" : "✅ Onayladınız") : (e ? "⛔ You rejected" : "⛔ Reddettiniz")) + $" · {DateTime.UtcNow.AddHours(3):dd.MM HH:mm}";
            var mine = await db.ChatMessages.Where(m => m.AppId == app.Id && m.StepId == stepId && m.State == "Open").ToListAsync(ct);
            foreach (var m in mine)
            {
                m.State = "Decided"; m.UpdatedAt = DateTime.UtcNow;
                try { await UpdateMessageAsync(app, m, StatusOf, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogInformation("Mesaj güncellenemedi: {Message}", ex.Message); }
            }
            app.LastActivityAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new(true, approve ? (en ? "Request approved." : "Talep onaylandı.") : (en ? "Request rejected." : "Talep reddedildi."), Status());
        }
        return (int)res.StatusCode switch
        {
            409 => new(false, Msg() ?? "Bu talep zaten karara bağlanmış.", Status()),
            403 => new(false, Msg() ?? "Bu talebi karara bağlama yetkiniz yok.", null),
            404 => new(false, Msg() ?? "Talep bulunamadı.", null),
            _ => new(false, $"Karar kaydedilemedi (HTTP {(int)res.StatusCode}). HR360 üzerinden deneyin.", null),
        };
    }

    // ------------------------------------------------------------------ komutlar

    public const string Help = "Komutlar: *onaylarım* (bekleyen onaylarınız) · *bakiye* (izin bakiyeniz) · *izin al* (izin talebi) · *izin iptal* · *özet* · *izindekiler* · *kimnerede* · *bekleyen* · *ben*"
        + " · *masraf* (fiş fotoğrafı gönderin) · *teşekkür @kişi mesaj* · *masa* · *vardiyam* · *takas* · *geldim* / *çıktım* · *duyurular* · *belge* · *bordrom* · *ik vakası* · *geçmişimi sil*."
        + " Ayrıca soru da sorabilirsiniz: \"sonraki resmî tatil ne zaman?\"";
    public const string HelpEn = "Commands: *approvals* (awaiting your decision) · *balance* (your leave balance) · *request leave* · *cancel leave* · *summary* · *on leave* · *whereabouts* · *pending* · *me*"
        + " · *expense* (send a receipt photo) · *thanks @person message* · *desk* · *my shifts* · *swaps* · *clock in* / *clock out* · *announcements* · *documents* · *payslip* · *hr case* · *forget me*."
        + " You can also ask a question: \"when is the next public holiday?\"";
    public static string HelpOf(bool en) => en ? HelpEn : Help;

    public async Task<ChatReply> CommandAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, string? input, CancellationToken ct,
        HrAssistant? assistant = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var provider = ChatMetrics.Provider(app.Platform);
        var tenant = who.TenantSlug;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        // BG7: yazım hatasına dayanıklı çözümleme (Türkçe karakter katlama + Levenshtein).
        var resolved = ChatCommands.Resolve(input);
        var cmd = resolved?.Cmd ?? "assistant";
        var englishCommand = resolved?.English ?? false;

        // Her istekte: çalışan hâlâ etkin mi (ayrılanın erişimi anında kapanır), hesap doğrulandı mı.
        var active = await EnsureActiveAsync(db, who, ct);
        // Dil: kişinin tercihi; tercih yoksa İngilizce yazılan komut İngilizce yanıtlanır.
        var en = active && await _sql.ScalarAsync("SELECT 1 FROM notification_preferences WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, tenant, who.EmployeeId!.Value) is not null
            ? await EnAsync(tenant, who.EmployeeId, ct)
            : englishCommand || LooksEnglish(input);
        string L(string trText, string enText) => en ? enText : trText;
        ChatReply T(string t) => new(t, Array.Empty<PendingApproval>()) { En = en };
        ChatReply Done(ChatReply r, string outcome)
        {
            ChatMetrics.Commands.WithLabels(provider, ChatMetrics.Intent(cmd), outcome).Inc();
            ChatMetrics.Latency.WithLabels(provider).Observe(sw.Elapsed.TotalSeconds);
            return r;
        }

        // BG14: kullanıcı ve kiracı başına hız sınırı (bellek içi kayan pencere).
        var now = DateTime.UtcNow;
        var (userOk, retry) = ChatFeatures.Limiter.Hit($"u:{app.Id}:{who.ExternalUserId}", ChatFeatures.UserLimit, ChatFeatures.RateWindow, now);
        var tenantOk = userOk && ChatFeatures.Limiter.Hit($"t:{tenant}", ChatFeatures.TenantLimit, ChatFeatures.RateWindow, now).Allowed;
        if (!userOk || !tenantOk)
        {
            ChatMetrics.RateLimited.WithLabels(provider, userOk ? "tenant" : "user").Inc();
            await Features.CountAsync(app, "ratelimit", userOk ? "tenant" : "user", ct);
            var secs = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds));
            return Done(T(userOk
                ? L("⏳ Şu an çok yoğunuz; birkaç saniye sonra yeniden deneyin.", "⏳ We are very busy right now; please try again in a few seconds.")
                : L($"⏳ Biraz yavaşlayalım: çok sık komut gönderdiniz. {secs} sn sonra yeniden deneyin.", $"⏳ Let's slow down a little: too many commands. Try again in {secs} s.")), "rate_limited");
        }

        var notLinked = L("Hesabınız bir çalışan kaydıyla eşleşmedi; bu komut için eşleşme gerekir. *ben* yazarak durumu görebilirsiniz.",
                          "Your account is not matched to an employee record; this command needs a match. Type *me* to see the status.");

        var known = cmd is "" or "help" or "me";
        if (!known && active && !Trusted(app, who))
            return Done(T(LinkPrompt(await LinkUrlAsync(db, who, TimeSpan.FromHours(24), ct), en: en)), "unverified");

        // BG20: kiracının bu uygulamada kapattığı komut.
        if (ChatFeatureCatalog.FeatureOf(cmd) is { } feature && app.DisabledFeatures.Contains(feature))
        {
            await Features.CountAsync(app, feature, "disabled", ct);
            return Done(T(L("Bu komut şirketinizde kapalı.", "This command is turned off in your company.")), "disabled");
        }

        ChatReply reply;
        try
        {
            reply = await RunCommandAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ChatApiException)
        {
            _log.LogWarning(ex, "Sohbet komutu işlenemedi ({Cmd})", cmd);
            await Features.ErrorAsync(app, cmd, ex.GetType().Name + ": " + ex.Message);
            return Done(T(L("⚠️ Bir hata oluştu; HR360 üzerinden deneyin.", "⚠️ Something went wrong; please try in HR360.")), "error");
        }
        await Features.CountAsync(app, ChatFeatureCatalog.FeatureOf(cmd) ?? (cmd.Length == 0 ? "help" : cmd), "ok", ct);
        if (resolved is { Fuzzy: true })
            reply = reply with { Text = L($"_(“{ChatFeatures.DisplayName(cmd, false)}” olarak anladım)_\n", $"_(understood as “{ChatFeatures.DisplayName(cmd, true)}”)_\n") + reply.Text };
        return Done(reply, "ok");

        async Task<ChatReply> RunCommandAsync()
        {
            if (ChatFeatures.OwnCommands.Contains(cmd))
            {
                if (who.EmployeeId is null) return T(notLinked);
                return await Features.CommandAsync(db, app, who, resolved!, en, ct);
            }
            switch (cmd)
            {
                case "" or "help":
                    return ChatFeatures.HelpReply(en);

                case "approvals":
                {
                    if (who.EmployeeId is null) return T(notLinked);
                    var items = new List<PendingApproval>();
                    foreach (var p in await MyPendingAsync(tenant, who.EmployeeId.Value, 5, ct)) items.Add(await Features.EnrichAsync(tenant, Shape(app, p, en), en, ct));
                    var total = items.Count < 5 ? items.Count : await MyPendingCountAsync(tenant, who.EmployeeId.Value, ct);
                    if (items.Count == 0) return T(L("Karar bekleyen talebiniz yok. 🎉", "Nothing is awaiting your decision. 🎉"));
                    // BG5: özet + "Tümünü onayla (n)" (onay adımı ve ek doğrulamayla).
                    var groups = (await MyPendingAsync(tenant, who.EmployeeId.Value, 50, ct)).GroupBy(i => i.Type).ToList();
                    var summary = string.Join(", ", groups.Select(g => $"{g.Count()} {TypeLabel(g.Key, en)}"));
                    List<ChatButton>? buttons = null;
                    if (total >= 2)
                    {
                        buttons = new() { new ChatButton(L($"✅ Tümünü onayla ({Math.Min(total, 50)})", $"✅ Approve all ({Math.Min(total, 50)})"), "approveall", "", "primary") };
                        // Birden çok tür varsa türe göre toplu onay da sunulur ("tüm izinleri onayla").
                        if (groups.Count > 1)
                            buttons.AddRange(groups.Where(g => g.Count() >= 2).Select(g => new ChatButton(
                                L($"Tüm {TypeLabel(g.Key, false)} taleplerini onayla ({g.Count()})", $"Approve all {TypeLabel(g.Key, true)} ({g.Count()})"), "approveall", g.Key)));
                    }
                    return new(total > items.Count
                        ? L($"Karar bekleyen *{total}* talebiniz var ({summary}); en yeni {items.Count} tanesi aşağıda. Tümü: {PublicOrigin}/panel/onaylar",
                            $"*{total}* requests are awaiting your decision ({summary}); the newest {items.Count} are below. All: {PublicOrigin}/panel/onaylar")
                        : L($"Karar bekleyen *{items.Count}* talebiniz var ({summary}):", $"*{items.Count}* request(s) awaiting your decision ({summary}):"), items) { En = en, Buttons = buttons };
                }

                case "balance":
                {
                    if (who.EmployeeId is null) return T(notLinked);
                    var lines = await BalanceLinesAsync(tenant, who.EmployeeId.Value, today.Year, en, ct);
                    if (lines.Count == 0) return T(L($"{today.Year} için tanımlı izin bakiyeniz yok.", $"You have no leave balance defined for {today.Year}."));
                    return ChatReply.Of(L($"*{today.Year} izin bakiyeniz*\n", $"*Your {today.Year} leave balance*\n") + string.Join("\n", lines), en,
                        ChatButton.Say(L("İzin al", "Request leave"), L("izin al", "request leave")), ChatButton.Link(L("Panelde aç", "Open in HR360"), $"{PublicOrigin}/panel/izin"));
                }

                case "leave":
                {
                    if (who.EmployeeId is null) return T(notLinked);
                    var lines = await BalanceLinesAsync(tenant, who.EmployeeId.Value, today.Year, en, ct);
                    return new(L("İzin talebi oluşturmak için formu açın.", "Open the form to request leave."), Array.Empty<PendingApproval>())
                        { En = en, Form = ChatForm.Leave, FormLines = lines };
                }

                case "home":
                    if (who.EmployeeId is null) return T(notLinked);
                    return T(await HomeTextAsync(tenant, who.EmployeeId.Value, en, ct));

                case "onleave":
                {
                    if (who.EmployeeId is null) return T(notLinked);
                    // KVKK veri en aza indirme: adlar yalnızca yöneticiye ve yalnızca kendi ekibi için;
                    // diğer çalışanlara toplam sayı. İzin türü hiçbir zaman yazılmaz.
                    var onLeave = await _sql.QueryAsync("""
                        SELECT l."EmployeeId", e."FirstName" || ' ' || e."LastName", l."EndDate" FROM leave_requests l JOIN employee_employees e ON e."Id" = l."EmployeeId"
                        WHERE l."TenantSlug" = $1 AND l."Status" = 'Approved' AND l."StartDate" <= $2 AND l."EndDate" >= $2 ORDER BY 2
                        """, r => (Id: r.GetGuid(0), Name: r.GetString(1), End: r.GetFieldValue<DateOnly>(2)), ct, tenant, today);
                    var team = (await _people.TeamOfAsync(tenant, who.EmployeeId.Value, ct)).Select(p => p.Id).ToHashSet();
                    if (team.Count == 0)
                        return T(onLeave.Count == 0 ? L("Bugün izinde olan kimse yok.", "Nobody is on leave today.")
                            : L($"Bugün şirkette *{onLeave.Count}* kişi izinde.", $"*{onLeave.Count}* people are on leave today."));
                    var mine = onLeave.Where(x => team.Contains(x.Id)).Select(x => en ? $"• {x.Name} (until {x.End:dd.MM})" : $"• {x.Name} ({x.End:dd.MM}'e kadar)").ToList();
                    return T((mine.Count == 0 ? L("Bugün ekibinizden izinde olan yok.", "Nobody from your team is on leave today.")
                            : L($"*Ekibinizden bugün izinde ({mine.Count}):*\n", $"*On leave today from your team ({mine.Count}):*\n") + string.Join("\n", mine))
                        + (onLeave.Count > mine.Count ? L($"\nŞirket genelinde toplam {onLeave.Count} kişi izinde.", $"\n{onLeave.Count} people are on leave company-wide.") : ""));
                }

                case "whereabouts":
                {
                    var modes = await _sql.QueryAsync("SELECT \"Mode\", count(*) FROM engagement_presence WHERE \"TenantSlug\" = $1 AND \"Date\" = $2 GROUP BY 1",
                        r => (Mode: r.GetString(0), N: r.GetInt64(1)), ct, tenant, today);
                    string M(string m) => m switch
                    {
                        "Office" => L("Ofiste", "In the office"), "Remote" => L("Uzaktan", "Remote"), "Travel" => L("Seyahatte", "Travelling"),
                        "Off" => L("Çalışmıyor", "Off"), _ => m,
                    };
                    return T(modes.Count == 0 ? L("Bugün için çalışma yeri bildiren olmadı.", "Nobody has shared where they work today.")
                        : L("*Bugün:* ", "*Today:* ") + string.Join(" · ", modes.Select(m => $"{M(m.Mode)} {m.N}")));
                }

                case "pending":
                {
                    var pending = Convert.ToInt64(await _sql.ScalarAsync("SELECT count(*) FROM workflow_requests WHERE \"TenantSlug\" = $1 AND \"Status\" = 'Pending'", ct, tenant));
                    return T(L($"Şirkette onay bekleyen *{pending}* talep var.", $"*{pending}* requests are awaiting approval company-wide."));
                }

                case "me":
                    if (who.EmployeeId is null)
                        return T(L($"Hesabınız ({who.Email ?? "e-posta görünmüyor"}) henüz bir çalışan kaydıyla eşleşmedi. Eşleşme e-posta adresiyle yapılır; HR360'taki e-postanız sohbet hesabınızla aynı olmalı.",
                                   $"Your account ({who.Email ?? "no email visible"}) is not matched to an employee record yet. Matching uses the email address; your HR360 email must be the same as your chat account's."));
                    if (!Trusted(app, who))
                        return T(LinkPrompt(await LinkUrlAsync(db, who, TimeSpan.FromHours(24), ct), en: en));
                    return T(L($"Hesabınız *{await NameOfAsync(tenant, who.EmployeeId.Value, ct)}* ({who.Email}) ile bağlı. Onay talepleri size buradan gelecek.",
                               $"Your account is linked to *{await NameOfAsync(tenant, who.EmployeeId.Value, ct)}* ({who.Email}). Approval requests will come here."));

                default:
                    return await AssistantAsync();
            }
        }

        async Task<ChatReply> AssistantAsync()
        {
            // Doğal dil: komut değilse İK asistanına sorulur (uygulama içi asistanla aynı mantık).
            var raw = (input ?? "").Trim();
            if (assistant is null || who.EmployeeId is null || !Trusted(app, who) || raw.Length is <= 1 or > 500)
                return ChatReply.Of(L("Bu komutu tanımadım. ", "I didn't recognise that command. ") + HelpOf(en), en);
            var emp = who.EmployeeId.Value;
            var me = await _people.FindAsync(tenant, emp, ct);
            var isManager = (await _people.TeamOfAsync(tenant, emp, ct)).Count > 0;
            // BG16: "peki geçen ay?" gibi devam soruları önceki soruyla birleştirilir (bağlam 30 gün, şifreli).
            var history = await Features.ContextAsync(tenant, emp, ct);
            var lastQuestion = history.LastOrDefault(h => h.Role == "user").Text;
            var question = FollowUp.Merge(lastQuestion, raw) ?? raw;
            // B19: yönetici rapor soruları yalnızca kendi departmanı için; kişi düzeyi yok (yalnızca İK, sohbette hiç);
            // 5'ten küçük gruplar gizlenir (NlReport varsayılanı).
            var dept = isManager && me?.DepartmentId is { } d ? d : (Guid?)null;
            var answer = await assistant.AskAsync(new AssistantAsker(tenant, me, me?.UserId, isManager, false, en, dept), question, ct);
            var text = SlackMd(answer.Reply);
            if (answer.Report is { Understood: true } rep) text += "\n" + ReportText(rep);
            var notUnderstood = answer.Source == "fallback" || answer.Report is { Understood: false };
            var misses = ChatFeatures.Miss(app.Id, who.ExternalUserId, notUnderstood);
            await Features.RememberAsync(tenant, emp, question, text, ct);
            // BG17: "Panelde aç" bağlantıları + hızlı yanıt önerileri.
            var buttons = new List<ChatButton>();
            buttons.AddRange(answer.Links.Take(3).Select(l => ChatButton.Link(L("Panelde aç: ", "Open: ") + l.Label, PublicOrigin + l.Path)));
            var suggestions = (answer.Report?.Suggestions ?? Array.Empty<string>()).Concat(answer.Related ?? Array.Empty<string>()).Take(3).ToList();
            buttons.AddRange(suggestions.Select(x => ChatButton.Say(x.Length > 40 ? x[..40] + "…" : x, x)));
            if (answer.Report is { Understood: true })
                buttons.Add(ChatButton.Say(L("Peki geçen ay?", "And last month?"), L("peki geçen ay", "and last month")));
            if (notUnderstood)
            {
                text += "\n\n" + HelpOf(en);
                // BG18: iki kez anlaşılamazsa İK vakası önerilir (kişi onaylamadan açılmaz).
                if (misses >= 2)
                {
                    var offer = await Features.HrCaseConfirmCardAsync(app, emp, raw, en, ct);
                    text += "\n\n" + offer.Text;
                    buttons.AddRange(offer.Buttons ?? Array.Empty<ChatButton>());
                    ChatFeatures.Miss(app.Id, who.ExternalUserId, false);
                }
                else if (buttons.Count == 0)
                {
                    buttons.Add(ChatButton.Say(L("Bakiye", "Balance"), L("bakiye", "balance")));
                    buttons.Add(ChatButton.Say(L("Sonraki tatil", "Next holiday"), L("sonraki resmi tatil ne zaman", "when is the next public holiday")));
                }
            }
            return new(text, Array.Empty<PendingApproval>()) { En = en, Buttons = buttons.Count == 0 ? null : buttons };
        }
    }

    /// <summary>Asistanın **kalın** biçimini Slack/Teams ortak *kalın* biçimine çevirir.</summary>
    private static string SlackMd(string s) => s.Replace("**", "*");

    /// <summary>Rapor sonucunun ilk satırları (sohbet için kısa tablo; kişi düzeyi yok, asistan yönetici değilse zaten kapalı).</summary>
    private static string ReportText(NlReport.Result r) =>
        string.Join("\n", r.Rows.Take(8).Select(row => "• " + string.Join(" · ", row.Select(c => c switch
        {
            decimal d => d.ToString("0.##", CultureInfo.InvariantCulture), double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            null => "—", var x => x.ToString(),
        })))) + (r.Rows.Count > 8 ? $"\n… (+{r.Rows.Count - 8})" : "");

    private static bool LooksEnglish(string? s) =>
        s is not null && System.Text.RegularExpressions.Regex.IsMatch(s.ToLowerInvariant(), @"\b(what|when|who|how|my|the|is|are|leave|balance|holiday|expenses?)\b")
        && !System.Text.RegularExpressions.Regex.IsMatch(s, "[çğıöşüÇĞİÖŞÜ]");

    public async Task<List<string>> BalanceLinesAsync(string tenant, Guid employeeId, int year, bool en, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync("""
            SELECT "Type", "EntitledDays", "UsedDays", "PendingDays" FROM leave_balances
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Year" = $3 ORDER BY "Type"
            """, r => (Type: r.GetString(0), E: r.GetDecimal(1), U: r.GetDecimal(2), P: r.GetDecimal(3)), ct, tenant, employeeId, year);
        return rows.Select(b => en
            ? $"• {LeaveTypeLabel(b.Type, true)}: *{b.E - b.U - b.P:0.#}* days left (entitled {b.E:0.#}, used {b.U:0.#}{(b.P > 0 ? $", pending {b.P:0.#}" : "")})"
            : $"• {LeaveLabel(b.Type)}: *{b.E - b.U - b.P:0.#}* gün kaldı (hak {b.E:0.#}, kullanılan {b.U:0.#}{(b.P > 0 ? $", onay bekleyen {b.P:0.#}" : "")})").ToList();
    }

    // ------------------------------------------------------------------ izin talebi (sohbetten)

    private static readonly string LeaveBase = (EnvVar.Or("LEAVE_SERVICE_URL", "http://leave-service:8080")).TrimEnd('/');

    /// <summary>
    /// Sohbetteki formdan izin talebi: leave-service'in iç ucu web ile aynı kuralları uygular
    /// (bakiye, çakışma, yıl sınırı) ve onay akışını başlatır. Yalnızca doğrulanmış hesap.
    /// </summary>
    public async Task<(bool Ok, string Message)> CreateLeaveAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who,
        string? type, string? start, string? end, string? reason, CancellationToken ct)
    {
        var en = await EnAsync(app.TenantSlug, who.EmployeeId, ct);
        string L(string a, string b) => en ? b : a;
        if (!await EnsureActiveAsync(db, who, ct) || !Trusted(app, who))
            return (false, L("Önce sohbet hesabınızı HR360'a bağlayın: *ben* yazın.", "First link your chat account to HR360: type *me*."));
        if (type is null || !ChatFormat.LeaveTypes.Contains(type)) return (false, L("İzin türünü seçin.", "Choose a leave type."));
        if (!DateOnly.TryParse(start, CultureInfo.InvariantCulture, out var s) || !DateOnly.TryParse(end, CultureInfo.InvariantCulture, out var e))
            return (false, L("Başlangıç ve bitiş tarihini seçin.", "Choose the start and end dates."));
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token)) return (false, L("Sunucuda INTERNAL_SERVICE_TOKEN tanımlı değil; sohbetten izin talebi kapalı.", "INTERNAL_SERVICE_TOKEN is not set; leave requests from chat are disabled."));
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{LeaveBase}/api/internal/leave-requests")
        {
            Content = JsonContent.Create(new
            {
                tenantSlug = app.TenantSlug, employeeId = who.EmployeeId, type, startDate = s.ToString("yyyy-MM-dd"), endDate = e.ToString("yyyy-MM-dd"),
                days = 0, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(500, reason.Trim().Length)],
            }),
        };
        req.Headers.Add("X-Internal-Token", token);
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        string? Msg() { try { return JsonDocument.Parse(body).RootElement.GetProperty("message").GetString(); } catch { return null; } }
        if (!res.IsSuccessStatusCode) return (false, Msg() ?? L($"İzin talebi oluşturulamadı (HTTP {(int)res.StatusCode}).", $"The leave request could not be created (HTTP {(int)res.StatusCode})."));
        decimal days = 0;
        try { days = JsonDocument.Parse(body).RootElement.GetProperty("days").GetDecimal(); } catch { }
        return (true, en
            ? $"✅ Your leave request was created: {LeaveTypeLabel(type, true)}, {s:dd.MM.yyyy} – {e:dd.MM.yyyy} ({days:0.#} working day(s)). You will be notified here when it is decided."
            : $"✅ İzin talebiniz oluşturuldu: {LeaveTypeLabel(type, false)}, {s:dd.MM.yyyy} – {e:dd.MM.yyyy} ({days:0.#} iş günü). Karar verilince buradan bildirilecek.");
    }

    // ------------------------------------------------------------------ özet (ana sayfa) ve sabah özeti

    public sealed record HomeData(List<string> Balance, long AwaitingMe, long DueToday, long MyPending, List<string> MyUpcoming, int TeamOff, bool IsManager);

    public async Task<HomeData> HomeAsync(string tenant, Guid employeeId, bool en, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var balance = await BalanceLinesAsync(tenant, employeeId, today.Year, en, ct);
        var awaiting = await MyPendingCountAsync(tenant, employeeId, ct);
        var dueToday = Convert.ToInt64(await _sql.ScalarAsync("""
            SELECT count(*) FROM workflow_requests w JOIN workflow_approval_steps s ON s."WorkflowRequestId" = w."Id" AND s."Decision" = 'Pending'
            WHERE w."TenantSlug" = $1 AND w."Status" = 'Pending' AND (s."ApproverEmployeeId" = $2 OR s."DelegatedToEmployeeId" = $2)
              AND w."SlaDueAt" IS NOT NULL AND w."SlaDueAt" < now() + interval '1 day'
            """, ct, tenant, employeeId));
        var myPending = Convert.ToInt64(await _sql.ScalarAsync(
            "SELECT count(*) FROM workflow_requests WHERE \"TenantSlug\" = $1 AND \"RequesterEmployeeId\" = $2 AND \"Status\" = 'Pending'", ct, tenant, employeeId));
        var upcoming = await _sql.QueryAsync("""
            SELECT "Type", "StartDate", "EndDate", "Status" FROM leave_requests
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Status" IN ('Approved','Submitted') AND "EndDate" >= $3 AND "StartDate" <= $3 + 60
            ORDER BY "StartDate" LIMIT 3
            """, r => $"• {LeaveTypeLabel(r.GetString(0), en)}: {r.GetFieldValue<DateOnly>(1):dd.MM} – {r.GetFieldValue<DateOnly>(2):dd.MM}{(r.GetString(3) == "Submitted" ? (en ? " (pending)" : " (onay bekliyor)") : "")}",
            ct, tenant, employeeId, today);
        var team = (await _people.TeamOfAsync(tenant, employeeId, ct)).Select(p => p.Id).ToArray();
        var teamOff = team.Length == 0 ? 0 : Convert.ToInt32(await _sql.ScalarAsync("""
            SELECT count(DISTINCT "EmployeeId") FROM leave_requests WHERE "TenantSlug" = $1 AND "Status" = 'Approved'
              AND "StartDate" <= $2 AND "EndDate" >= $2 AND "EmployeeId" = ANY($3)
            """, ct, tenant, today, team));
        return new(balance, awaiting, dueToday, myPending, upcoming, teamOff, team.Length > 0);
    }

    public async Task<string> HomeTextAsync(string tenant, Guid employeeId, bool en, CancellationToken ct)
    {
        var h = await HomeAsync(tenant, employeeId, en, ct);
        string L(string a, string b) => en ? b : a;
        var parts = new List<string> { L("*Özetiniz*", "*Your summary*") };
        if (h.AwaitingMe > 0) parts.Add(L($"🗳 Kararınızı bekleyen *{h.AwaitingMe}* talep", $"🗳 *{h.AwaitingMe}* request(s) awaiting your decision")
            + (h.DueToday > 0 ? L($" ({h.DueToday} tanesinin süresi 24 saat içinde doluyor)", $" ({h.DueToday} due within 24 hours)") : "") + L(" — *onaylarım*", " — *approvals*"));
        if (h.MyPending > 0) parts.Add(L($"⏳ Onay bekleyen *{h.MyPending}* talebiniz var", $"⏳ You have *{h.MyPending}* request(s) awaiting approval"));
        if (h.IsManager) parts.Add(h.TeamOff == 0 ? L("👥 Bugün ekibinizden izinde olan yok", "👥 Nobody from your team is on leave today")
            : L($"👥 Bugün ekibinizden *{h.TeamOff}* kişi izinde — *izindekiler*", $"👥 *{h.TeamOff}* of your team on leave today — *on leave*"));
        if (h.MyUpcoming.Count > 0) parts.Add(L("🌴 Yaklaşan izinleriniz:\n", "🌴 Your upcoming leave:\n") + string.Join("\n", h.MyUpcoming));
        if (h.Balance.Count > 0) parts.Add(L("📊 İzin bakiyeniz:\n", "📊 Your leave balance:\n") + string.Join("\n", h.Balance));
        parts.Add(L("İzin almak için *izin al* yazın.", "To request leave, type *request leave*."));
        return string.Join("\n", parts);
    }

    /// <summary>Slack App Home görünümü (kişiye özel; başkası göremez).</summary>
    public async Task<JsonObject> SlackHomeViewAsync(ChatApp app, ChatIdentity who, CancellationToken ct)
    {
        var blocks = new JsonArray();
        JsonObject Section(string t) => new() { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = t } };
        if (who.EmployeeId is null || !Trusted(app, who))
        {
            var en0 = await EnAsync(app.TenantSlug, who.EmployeeId, ct);
            blocks.Add(Section(who.EmployeeId is null
                ? (en0 ? "Your Slack account is not matched to an HR360 employee yet. Type *me* to the bot." : "Slack hesabınız henüz bir HR360 çalışanıyla eşleşmedi. Bota *ben* yazın.")
                : (en0 ? "🔒 Link your account to see your summary here: type *me* to the bot." : "🔒 Özetinizi burada görmek için hesabınızı bağlayın: bota *ben* yazın.")));
        }
        else
        {
            var en = await EnAsync(app.TenantSlug, who.EmployeeId, ct);
            blocks.Add(new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "HR360" } });
            blocks.Add(Section(await HomeTextAsync(app.TenantSlug, who.EmployeeId.Value, en, ct)));
            blocks.Add(new JsonObject { ["type"] = "actions", ["elements"] = new JsonArray
            {
                new JsonObject { ["type"] = "button", ["action_id"] = ChatFormat.LeaveFormAction, ["style"] = "primary", ["value"] = "leave",
                    ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = en ? "Request leave" : "İzin talebi oluştur" } },
                new JsonObject { ["type"] = "button", ["action_id"] = "hr360_open", ["url"] = $"{PublicOrigin}/panel/onaylar",
                    ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = en ? "Approvals" : "Onay kutusu" } },
                new JsonObject { ["type"] = "button", ["action_id"] = "hr360_open", ["url"] = $"{PublicOrigin}/panel",
                    ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = en ? "Open HR360" : "HR360'ı aç" } },
            } });
        }
        return new JsonObject { ["type"] = "home", ["blocks"] = blocks };
    }

    /// <summary>
    /// Sabah özeti: kararı bekleyen talepler ve ekipten izinde olanlar. Söylenecek bir şey yoksa
    /// null (mesaj gönderilmez). İzin türü ve kişi adı yazılmaz; ayrıntı HR360'ta.
    /// </summary>
    public async Task<string?> DigestTextAsync(string tenant, Guid employeeId, bool en, CancellationToken ct)
    {
        var h = await HomeAsync(tenant, employeeId, en, ct);
        if (h.AwaitingMe == 0 && h.TeamOff == 0) return null;
        string L(string a, string b) => en ? b : a;
        var lines = new List<string> { L("☀️ *Günaydın!* Bugünün özeti:", "☀️ *Good morning!* Today's summary:") };
        if (h.AwaitingMe > 0) lines.Add(L($"• Kararınızı bekleyen *{h.AwaitingMe}* talep", $"• *{h.AwaitingMe}* request(s) awaiting your decision")
            + (h.DueToday > 0 ? L($", {h.DueToday} tanesinin süresi bugün doluyor", $", {h.DueToday} due today") : ""));
        if (h.TeamOff > 0) lines.Add(L($"• Ekibinizden bugün *{h.TeamOff}* kişi izinde", $"• *{h.TeamOff}* of your team on leave today"));
        lines.Add(L("Ayrıntı için *onaylarım* ya da *izindekiler* yazın.", "Type *approvals* or *on leave* for details."));
        return string.Join("\n", lines);
    }

    /// <summary>Uygulamanın doğrulanmış kullanıcılarına sabah özetini gönderir (kişi başına günde bir).</summary>
    public async Task<int> SendDigestsAsync(GovernanceDbContext db, ChatApp app, bool force, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var ids = await db.ChatIdentities.IgnoreQueryFilters()
            .Where(i => i.AppId == app.Id && i.EmployeeId != null && i.ConversationId != null && (force || i.LastDigestOn == null || i.LastDigestOn < today))
            .ToListAsync(ct);
        var sent = 0;
        var seen = new HashSet<Guid>();
        foreach (var i in ids)
        {
            if (!Trusted(app, i) || !seen.Add(i.EmployeeId!.Value)) continue;
            var en = await EnAsync(app.TenantSlug, i.EmployeeId, ct);
            var text = await DigestTextAsync(app.TenantSlug, i.EmployeeId.Value, en, ct);
            i.LastDigestOn = today;
            if (text is null) continue;
            try
            {
                if (await SendTextToEmployeeAsync(db, app, i.EmployeeId.Value, text, $"{PublicOrigin}/panel/onaylar", ct)) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { await FailAsync(db, app, ex, ct); }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

}

/// <summary>
/// Sabah özeti: hafta içi 09:00'dan (Türkiye saati) sonra, açık olan uygulamaların doğrulanmış
/// kullanıcılarına günde bir kez gönderilir. Uygulama ayarından kapatılabilir.
/// </summary>
public sealed class ChatDigestWorker(IServiceProvider sp, ILogger<ChatDigestWorker> log) : BackgroundService
{
    public static readonly int Hour = int.TryParse(EnvVar.Or("CHAT_DIGEST_HOUR", "9"), out var h) && h is >= 0 and <= 23 ? h : 9;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow.AddHours(3);
                if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && now.Hour >= Hour && now.Hour < 18)
                {
                    using var scope = sp.CreateScope();
                    scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
                    var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
                    var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
                    var apps = await db.ChatApps.IgnoreQueryFilters().Where(a => a.IsEnabled && a.DailyDigest).ToListAsync(ct);
                    foreach (var app in apps)
                    {
                        if (!ChatHosts.Allowed(app, await TransferGuard.AllowedAsync(db, app.TenantSlug, ct))) continue;
                        await chat.SendDigestsAsync(db, app, false, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Sabah özeti gönderilemedi"); }
            await Task.Delay(TimeSpan.FromMinutes(10), ct);
        }
    }
}

/// <summary>Sohbet gönderim kuyruğunu periyodik olarak işler.</summary>
public sealed class ChatOutboxWorker(IServiceProvider sp, ILogger<ChatOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Clamp(ChatService.RetryBaseSeconds / 2, 1, 30));
        // Dalga 5e: zamanlanmış bot işleri de bu döngüden (CHAT_JOBS_SECONDS, varsayılan 5 dk) çalışır.
        var lastJobs = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
                var db = scope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
                var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
                await chat.ProcessOutboxAsync(db, ct);
                if (DateTime.UtcNow - lastJobs >= TimeSpan.FromSeconds(ChatFeatures.JobsSeconds))
                {
                    lastJobs = DateTime.UtcNow;
                    await chat.Features.RunAllJobsAsync(db, null, false, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Sohbet kuyruğu işlenemedi"); }
            await Task.Delay(period, ct);
        }
    }
}
