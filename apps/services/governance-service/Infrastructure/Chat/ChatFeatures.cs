using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure.Chat;

/* ======================================================================
 * Dalga 5e — sohbet botu özellikleri (çekirdek): sağlayıcıdan bağımsız
 * gönderim (Slack, Teams, Mattermost, Rocket.Chat), sessiz saat (BG6),
 * bekleyen işlemler ve adım doğrulaması (BG13), kullanım sayaçları (BG20),
 * hız sınırı (BG14), konuşma bağlamı (BG16), eylem düğmeleri (BG10).
 * İş özellikleri ChatFeaturesWork.cs'te, zamanlanmış işler ChatJobs.cs'tedir.
 *
 * Diğer modüllere YAZMA: bu servislerin sohbet adına (kullanıcı jetonu olmadan)
 * çağrılabilecek iç uçları yoktur. Bot, bağlı ve doğrulanmış çalışanın KENDİ
 * kaydı için, sahibi olan servisin kurallarını burada aynen uygulayarak ortak
 * veritabanına yazar ve her yazmayı audit_log'a "governance-service/chat"
 * olarak kaydeder (iç uç varsa — izin talebi, onay kararı — o kullanılır).
 * ==================================================================== */
public sealed partial class ChatFeatures
{
    private readonly ChatService _chat;
    private readonly Sql _sql;
    private readonly SlackApi _slack;
    private readonly TeamsApi _teams;
    private readonly MattermostApi _mm;
    private readonly RocketChatApi _rc;
    private readonly IHttpClientFactory _http;
    private readonly PeopleDirectory _people;
    private readonly ILogger _log;

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static string Origin => ChatService.PublicOrigin;
    private static readonly string NotificationBase = EnvVar.Or("NOTIFICATION_SERVICE_URL", "http://notification-service:8080").TrimEnd('/');

    // BG14: hız sınırı (kullanıcı başına ve kiracı başına, kayan pencere).
    public static readonly SlidingWindowLimiter Limiter = new();
    public static readonly int UserLimit = int.TryParse(EnvVar.Or("CHAT_RATE_USER", "20"), out var u) && u > 0 ? u : 20;
    public static readonly int TenantLimit = int.TryParse(EnvVar.Or("CHAT_RATE_TENANT", "300"), out var t) && t > 0 ? t : 300;
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(int.TryParse(EnvVar.Or("CHAT_RATE_WINDOW_SECONDS", "60"), out var w) && w > 0 ? w : 60);

    /// <summary>BG16: hatırlanan tur sayısı ve saklama süresi.</summary>
    public const int ContextTurns = 6;
    public const int ContextDays = 30;

    public ChatFeatures(ChatService chat, Sql sql, SlackApi slack, TeamsApi teams, IHttpClientFactory http, PeopleDirectory people, ILogger log)
    {
        _chat = chat; _sql = sql; _slack = slack; _teams = teams; _http = http; _people = people; _log = log;
        _mm = new MattermostApi(http);
        _rc = new RocketChatApi(http);
    }

    public MattermostApi Mattermost => _mm;
    public RocketChatApi RocketChat => _rc;

    private static string Trim(string? s, int n) => s is null ? "" : s.Length > n ? s[..n] : s;

    // ------------------------------------------------------------------ BG20 sayaçlar ve hatalar

    /// <summary>Günlük kullanım sayacı (yalnızca sayı; kişi, metin yok).</summary>
    public async Task CountAsync(ChatApp app, string feature, string outcome, CancellationToken ct)
    {
        try
        {
            await _sql.ExecuteAsync("""
                INSERT INTO governance_chat_usage ("TenantSlug","AppId","Day","Feature","Outcome","Count") VALUES ($1,$2,$3,$4,$5,1)
                ON CONFLICT ("TenantSlug","AppId","Day","Feature","Outcome") DO UPDATE SET "Count" = governance_chat_usage."Count" + 1
                """, ct, app.TenantSlug, app.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), Trim(feature, 40), Trim(outcome, 20));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Sohbet sayacı yazılamadı"); }
    }

    /// <summary>Son hatalar (uygulama başına en çok 20; mesajda kişisel veri olmamalı — yalnızca teknik ileti).</summary>
    public async Task ErrorAsync(ChatApp app, string feature, string message)
    {
        try
        {
            await _sql.ExecuteAsync("INSERT INTO governance_chat_errors (\"Id\",\"TenantSlug\",\"AppId\",\"Feature\",\"Message\",\"At\") VALUES ($1,$2,$3,$4,$5,now())",
                CancellationToken.None, Guid.NewGuid(), app.TenantSlug, app.Id, Trim(feature, 40), Trim(message, 300));
            await _sql.ExecuteAsync("""
                DELETE FROM governance_chat_errors WHERE "AppId" = $1 AND "Id" NOT IN
                  (SELECT "Id" FROM governance_chat_errors WHERE "AppId" = $1 ORDER BY "At" DESC LIMIT 20)
                """, CancellationToken.None, app.Id);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Sohbet hatası kaydedilemedi"); }
    }

    // ------------------------------------------------------------------ denetim kaydı

    /// <summary>Botun başka modüle yazdığı her işlem: kim (çalışan), ne, hangi kayıt. Alan değerleri yazılmaz.</summary>
    public async Task AuditAsync(string tenant, Guid employeeId, string entityType, string entityId, string action, object changes, string platform)
    {
        try
        {
            await _sql.ExecuteAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ($1,'governance-service/chat',$2,$3,$4,$5::jsonb,$6,$7,$8,NULL,now())", CancellationToken.None,
                tenant, entityType, entityId, action, JsonSerializer.Serialize(changes), "employee:" + employeeId, $"Sohbet ({platform})", Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Sohbet denetim kaydı yazılamadı"); }
    }

    // ------------------------------------------------------------------ bekleyen işlemler

    public sealed record Pending(Guid Id, string Kind, JsonElement Payload, string State, Guid AppId, Guid EmployeeId, string? Summary, DateTime ExpiresAt);

    public async Task<Guid> PendingAddAsync(ChatApp app, Guid employeeId, string kind, object payload, TimeSpan ttl, string? summary, CancellationToken ct, string? codeHash = null)
    {
        var id = Guid.NewGuid();
        await _sql.ExecuteAsync("""
            INSERT INTO governance_chat_pending ("Id","TenantSlug","AppId","EmployeeId","Kind","PayloadEnc","State","CodeHash","Summary","CreatedAt","ExpiresAt")
            VALUES ($1,$2,$3,$4,$5,$6,'Pending',$7,$8,now(),$9)
            """, ct, id, app.TenantSlug, app.Id, employeeId, kind, SecretBox.Protect(JsonSerializer.Serialize(payload))!, codeHash, summary, DateTime.UtcNow + ttl);
        return id;
    }

    private static Pending MapPending(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1),
        JsonDocument.Parse(SecretBox.Unprotect(r.GetString(2)) ?? "{}").RootElement.Clone(), r.GetString(3), r.GetGuid(4), r.GetGuid(5), r.Str(6), r.GetFieldValue<DateTime>(7));

    private const string PendingCols = "\"Id\",\"Kind\",\"PayloadEnc\",\"State\",\"AppId\",\"EmployeeId\",\"Summary\",\"ExpiresAt\"";

    /// <summary>Yalnızca sahibi olan çalışan için, süresi dolmamış ve beklemedeki kayıt.</summary>
    public async Task<Pending?> PendingGetAsync(string tenant, Guid employeeId, Guid id, CancellationToken ct) =>
        (await _sql.QueryAsync($"""
            SELECT {PendingCols} FROM governance_chat_pending
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Id" = $3 AND "State" = 'Pending' AND "ExpiresAt" > now()
            """, MapPending, ct, tenant, employeeId, id)).FirstOrDefault();

    /// <summary>Durumu yalnızca hâlâ beklemedeyse değiştirir (çift tıklamada tek işlem). Değiştiyse true.</summary>
    public async Task<bool> PendingCloseAsync(Guid id, string state, CancellationToken ct) =>
        await _sql.ExecuteAsync("UPDATE governance_chat_pending SET \"State\" = $2, \"ConfirmedAt\" = now(), \"PayloadEnc\" = CASE WHEN $2 = 'Cancelled' THEN '' ELSE \"PayloadEnc\" END WHERE \"Id\" = $1 AND \"State\" = 'Pending'",
            ct, id, state) > 0;

    // ------------------------------------------------------------------ BG13 adım doğrulaması

    public static string Hash(string s) => ChatService.HashCode(s);

    /// <summary>
    /// Hassas işlem (bordro özeti, toplu ya da ücretle ilgili onay): HR360'ta (oturum açık) onay
    /// için tek kullanımlık bağlantı. Panel ayrıca 6 haneli kod gösterir; kod sohbete yazılabilir.
    /// Onaylanana kadar işlem bekler (10 dk).
    /// </summary>
    public async Task<ChatReply> StepUpAsync(ChatApp app, Guid employeeId, string kind, object payload, string summary, bool en, CancellationToken ct)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await PendingAddAsync(app, employeeId, "stepup:" + kind, payload, TimeSpan.FromMinutes(10), Trim(summary, 200), ct, Hash(code));
        var url = $"{Origin}/panel/sohbet-onay?kod={code}";
        await CountAsync(app, "stepup", "requested", ct);
        return ChatReply.Of(en
                ? $"🔐 This action needs extra verification: *{summary}*. Confirm it in HR360 (sign-in required): {url} — valid for 10 minutes. Or type the 6-digit code shown in HR360 › Chat verification as *code 123456*."
                : $"🔐 Bu işlem ek doğrulama gerektiriyor: *{summary}*. HR360'ta (giriş yaparak) onaylayın: {url} — 10 dakika geçerli. Ya da HR360 › Sohbet doğrulaması ekranındaki 6 haneli kodu *kod 123456* biçiminde yazın.",
            en, ChatButton.Link(en ? "Verify in HR360" : "HR360'ta doğrula", url)) with { Replace = true };
    }

    /// <summary>Bağlantı koduyla bekleyen adım doğrulaması (web sayfası önizleme/onay).</summary>
    public async Task<Pending?> StepUpByCodeAsync(string code, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code) || code.Length > 100 ? null : (await _sql.QueryAsync($"""
            SELECT {PendingCols} FROM governance_chat_pending
            WHERE "CodeHash" = $1 AND "State" = 'Pending' AND "ExpiresAt" > now() AND "Kind" LIKE 'stepup:%'
            """, MapPending, ct, Hash(code.Trim()))).FirstOrDefault();

    /// <summary>Panelde gösterilecek 6 haneli kodlar (oturumdaki çalışanın bekleyen doğrulamaları; 5 dk).</summary>
    public async Task<List<(Guid Id, string Summary, string Otp, DateTime ExpiresAt)>> StepUpOtpsAsync(string tenant, Guid employeeId, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync("""
            SELECT "Id", coalesce("Summary", ''), "ExpiresAt" FROM governance_chat_pending
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "State" = 'Pending' AND "ExpiresAt" > now() AND "Kind" LIKE 'stepup:%'
            ORDER BY "CreatedAt" DESC LIMIT 5
            """, r => (Id: r.GetGuid(0), Summary: r.GetString(1), Exp: r.GetFieldValue<DateTime>(2)), ct, tenant, employeeId);
        var list = new List<(Guid, string, string, DateTime)>();
        foreach (var r in rows)
        {
            var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
            var exp = DateTime.UtcNow.AddMinutes(5) < r.Exp ? DateTime.UtcNow.AddMinutes(5) : r.Exp;
            await _sql.ExecuteAsync("UPDATE governance_chat_pending SET \"OtpHash\" = $2, \"OtpExpiresAt\" = $3 WHERE \"Id\" = $1", ct, r.Id, Hash($"{r.Id}:{otp}"), exp);
            list.Add((r.Id, r.Summary, otp, exp));
        }
        return list;
    }

    /// <summary>Sohbete yazılan "kod 123456": kişinin bekleyen doğrulamalarından eşleşeni onaylar.</summary>
    private async Task<ChatReply> CodeAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, string args, bool en, CancellationToken ct)
    {
        string L(string a, string b) => en ? b : a;
        var otp = new string((args ?? "").Where(char.IsDigit).ToArray());
        if (otp.Length != 6) return ChatReply.Of(L("Kodu 6 hane olarak yazın: *kod 123456*", "Type the 6-digit code: *code 123456*"), en);
        var rows = await _sql.QueryAsync("""
            SELECT "Id", "OtpHash" FROM governance_chat_pending
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "State" = 'Pending' AND "ExpiresAt" > now() AND "OtpExpiresAt" > now() AND "Kind" LIKE 'stepup:%'
            """, r => (Id: r.GetGuid(0), H: r.Str(1)), ct, who.TenantSlug, who.EmployeeId!.Value);
        var hit = rows.FirstOrDefault(r => r.H is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(r.H), Encoding.UTF8.GetBytes(Hash($"{r.Id}:{otp}"))));
        if (hit.Id == Guid.Empty)
        {
            await CountAsync(app, "stepup", "bad_code", ct);
            return ChatReply.Of(L("Kod geçersiz ya da süresi dolmuş. HR360 › Sohbet doğrulaması ekranından yeni kod alın.", "The code is invalid or expired. Get a new one in HR360 › Chat verification."), en);
        }
        var (_, message) = await ConfirmStepUpAsync(db, who.TenantSlug, who.EmployeeId!.Value, hit.Id, notify: false, ct);
        return ChatReply.Of(message, en);
    }

    /// <summary>Doğrulanan işlemi yürütür. notify: sonuç sohbete de yazılsın mı (web'den onayda evet).</summary>
    public async Task<(bool Ok, string Message)> ConfirmStepUpAsync(GovernanceDbContext db, string tenant, Guid employeeId, Guid pendingId, bool notify, CancellationToken ct)
    {
        var p = await PendingGetAsync(tenant, employeeId, pendingId, ct);
        if (p is null || !p.Kind.StartsWith("stepup:")) return (false, "Doğrulama bulunamadı ya da süresi dolmuş.");
        if (!await PendingCloseAsync(p.Id, "Confirmed", ct)) return (false, "Bu doğrulama zaten kullanıldı.");
        var app = await db.ChatApps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == p.AppId && a.IsEnabled, ct);
        if (app is null) return (false, "Sohbet uygulaması kapalı.");
        var who = await db.ChatIdentities.IgnoreQueryFilters().Where(i => i.AppId == app.Id && i.EmployeeId == employeeId).OrderByDescending(i => i.LastSeenAt).FirstOrDefaultAsync(ct);
        if (who is null) return (false, "Sohbet hesabı bulunamadı.");
        var en = await _chat.EnAsync(tenant, employeeId, ct);
        await CountAsync(app, "stepup", "confirmed", ct);
        string message;
        switch (p.Kind)
        {
            case "stepup:decide":
            {
                var wf = p.Payload.GetProperty("wf").GetGuid();
                var step = p.Payload.GetProperty("step").GetGuid();
                var approve = p.Payload.GetProperty("approve").GetBoolean();
                var r = await _chat.DecideAsync(db, app, who, wf, step, approve, ct, null, stepUpDone: true);
                message = (r.Ok ? "✅ " : "⚠️ ") + r.Message;
                break;
            }
            case "stepup:bulk":
                message = await BulkApproveAsync(db, app, who, p.Payload, en, ct);
                break;
            case "stepup:payslip":
                message = await PayslipTextAsync(tenant, employeeId, en, ct);
                break;
            default:
                return (false, "Bilinmeyen işlem.");
        }
        if (notify)
        {
            try { await DeliverAsync(db, app, employeeId, ChatReply.Of(message, en), ct); await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogInformation("Doğrulama sonucu sohbete yazılamadı: {Message}", ex.Message); }
        }
        return (true, message);
    }

    // ------------------------------------------------------------------ gönderim (dört sağlayıcı)

    /// <summary>Sohbette DM alabilen, doğrulanmış kimlik (Slack/MM/RC'de gerekirse bulunur ve DM açılır).</summary>
    public async Task<ChatIdentity?> IdentityForEmployeeAsync(GovernanceDbContext db, ChatApp app, Guid employeeId, CancellationToken ct)
    {
        ChatIdentity? id;
        switch (app.Platform)
        {
            case "Slack":
                id = await _chat.SlackIdentityForEmployeeAsync(db, app, employeeId, null, ct);
                break;
            case "Teams":
                id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == employeeId && i.ConversationId != null, ct);
                break;
            case "Mattermost":
                id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == employeeId, ct);
                if (id is not null && id.ConversationId is null)
                    id.ConversationId = await _mm.DirectChannelAsync(app.ServerUrl!, SecretBox.Unprotect(app.BotTokenEnc)!, app.BotUserId!, id.ExternalUserId, ct);
                break;
            case "RocketChat":
                id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == employeeId, ct);
                if (id is not null && id.ConversationId is null)
                {
                    var token = SecretBox.Unprotect(app.BotTokenEnc)!;
                    var (_, _, username) = await _rc.UserAsync(app.ServerUrl!, app.BotUserId!, token, id.ExternalUserId, ct);
                    if (username is not null) id.ConversationId = await _rc.DirectRoomAsync(app.ServerUrl!, app.BotUserId!, token, username, ct);
                }
                break;
            default:
                return null;
        }
        return id is not null && id.ConversationId is not null && ChatService.Trusted(app, id) ? id : null;
    }

    /// <summary>Kişiye özel mesaj (sessiz saat denetimi yok). Eşleşme yoksa false.</summary>
    public async Task<bool> DeliverAsync(GovernanceDbContext db, ChatApp app, Guid employeeId, ChatReply reply, CancellationToken ct)
    {
        var id = await IdentityForEmployeeAsync(db, app, employeeId, ct);
        if (id is null) return false;
        await PostAsync(app, id.ConversationId!, id.ServiceUrl, reply, ct);
        app.LastActivityAt = DateTime.UtcNow;
        ChatMetrics.Sent.WithLabels(ChatMetrics.Provider(app.Platform), "dm", "sent").Inc();
        return true;
    }

    /// <summary>Bir konuşmaya (DM ya da kanal) yanıt gönderir; mesaj kimliğini döndürür.</summary>
    public async Task<string?> PostAsync(ChatApp app, string conversationId, string? serviceUrl, ChatReply reply, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        switch (app.Platform)
        {
            case "Slack":
                return await _slack.PostAsync(SecretBox.Unprotect(app.SlackBotTokenEnc)!, conversationId, reply.Text, ChatFormat.SlackReply(reply), ct);
            case "Teams":
            {
                var token = await _chat.TeamsTokenAsync(app, ct);
                var url = serviceUrl ?? await TeamsServiceUrlAsync(app, ct) ?? throw new ChatApiException("Teams serviceUrl bilinmiyor");
                string? first = null;
                foreach (var a in ChatFormat.TeamsReply(reply)) first ??= await _teams.PostActivityAsync(token, url, conversationId, a, ct);
                return first;
            }
            case "Mattermost":
                return await _mm.PostAsync(app.ServerUrl!, SecretBox.Unprotect(app.BotTokenEnc)!, conversationId, MmText(reply, "Mattermost"),
                    ChatCards.MattermostAttachments(app, "", AllButtons(reply), now), ct);
            case "RocketChat":
                return await _rc.PostAsync(app.ServerUrl!, app.BotUserId!, SecretBox.Unprotect(app.BotTokenEnc)!, conversationId, MmText(reply, "RocketChat"),
                    ChatCards.RocketAttachments(AllButtons(reply), now), ct);
        }
        return null;
    }

    /// <summary>Kanal mesajı için Teams adresi: bilinen bir konuşmanın serviceUrl'i.</summary>
    private async Task<string?> TeamsServiceUrlAsync(ChatApp app, CancellationToken ct) =>
        await _sql.ScalarAsync("SELECT \"ServiceUrl\" FROM governance_chat_identities WHERE \"AppId\" = $1 AND \"ServiceUrl\" IS NOT NULL ORDER BY \"LastSeenAt\" DESC NULLS LAST LIMIT 1", ct, app.Id) as string;

    /// <summary>Mattermost/Rocket.Chat'te form yerine metin + düğme: onay listesi ve bağlantılar düğmeye dönüşür.</summary>
    public static IReadOnlyList<ChatButton> AllButtons(ChatReply reply)
    {
        var list = new List<ChatButton>();
        if (reply.Link is not null) list.Add(ChatButton.Link(reply.En ? "Open in HR360" : "HR360'ta aç", reply.Link));
        if (reply.Form == ChatForm.Leave) list.Add(ChatButton.Link(reply.En ? "Request leave in HR360" : "HR360'ta izin talebi", $"{Origin}/panel/izin"));
        if (reply.Form == ChatForm.Expense && reply.Expense is { } d)
        {
            list.Add(new ChatButton(reply.En ? "Create draft" : "Taslak oluştur", "exp_confirm", d.PendingId.ToString(), "primary"));
            list.Add(new ChatButton(reply.En ? "Cancel" : "Vazgeç", "exp_cancel", d.PendingId.ToString()));
        }
        if (reply.Buttons is not null) list.AddRange(reply.Buttons);
        foreach (var p in reply.Approvals) list.AddRange(ChatCards.ApprovalButtons(p, reply.En).Where(b => b.Url is null));
        return list;
    }

    public static string MmText(ChatReply reply, string platform)
    {
        var text = reply.Text;
        foreach (var p in reply.Approvals) text += "\n\n" + ChatCards.ApprovalText(p, reply.En);
        return ChatCards.MarkdownFor(platform, text);
    }

    // ------------------------------------------------------------------ BG6 sessiz saat

    public enum SendOutcome { Sent, Deferred, Skipped, NoIdentity }

    /// <summary>
    /// Kritik olmayan bot mesajı: kişinin bildirim tercihine göre (sohbet kanalı, sessiz saat) gönderilir,
    /// ertelenir (gönderim kuyruğu) ya da atlanır. Tercih servisine ulaşılamazsa gönderilir.
    /// </summary>
    public async Task<SendOutcome> SendAsync(GovernanceDbContext db, ChatApp app, Guid employeeId, ChatReply reply, string templateCode, bool critical, CancellationToken ct)
    {
        if (app.RespectQuietHours && !critical)
        {
            var (chatOn, until) = await EffectiveAsync(app.TenantSlug, employeeId, templateCode, ct);
            var (action, deferUntil) = QuietHours.Decide(false, chatOn, until, DateTimeOffset.UtcNow);
            if (action == QuietHours.Action.Skip) { ChatMetrics.Sent.WithLabels(ChatMetrics.Provider(app.Platform), "dm", "skipped").Inc(); return SendOutcome.Skipped; }
            if (action == QuietHours.Action.Defer)
            {
                db.ChatOutbox.Add(new ChatOutbox
                {
                    TenantSlug = app.TenantSlug, AppId = app.Id, Kind = "card", Attempts = 0, NextAttemptAt = deferUntil!.Value.UtcDateTime,
                    Payload = JsonSerializer.Serialize(new OutboxCard(employeeId, templateCode, Serialize(reply))),
                    LastError = "Sessiz saat: ertelendi",
                });
                ChatMetrics.Sent.WithLabels(ChatMetrics.Provider(app.Platform), "dm", "deferred").Inc();
                return SendOutcome.Deferred;
            }
        }
        return await DeliverAsync(db, app, employeeId, reply, ct) ? SendOutcome.Sent : SendOutcome.NoIdentity;
    }

    public sealed record OutboxCard(Guid EmployeeId, string TemplateCode, JsonObject Reply);

    /// <summary>notification-service "effective" ucu: sohbet kanalı açık mı, sessiz saat ne zaman bitiyor.</summary>
    public async Task<(bool ChatOn, DateTimeOffset? DeferUntil)> EffectiveAsync(string tenant, Guid employeeId, string templateCode, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token)) return (true, null);
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(4);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{NotificationBase}/api/notifications/preferences/effective?employeeId={employeeId}&tenant={Uri.EscapeDataString(tenant)}&templateCode={Uri.EscapeDataString(templateCode)}");
            req.Headers.Add("X-Internal-Token", token);
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (true, null);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            if (!root.TryGetProperty("decision", out var d) || d.ValueKind != JsonValueKind.Object) return (true, null);
            var chatOn = !d.TryGetProperty("chat", out var c) || c.ValueKind != JsonValueKind.False;
            DateTimeOffset? until = d.TryGetProperty("chatDeferUntil", out var u) && u.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x) ? x : null;
            return (chatOn, until);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug("Bildirim tercihi okunamadı, mesaj gönderilecek: {Message}", ex.Message);
            return (true, null);
        }
    }

    public static JsonObject Serialize(ChatReply r) => new()
    {
        ["text"] = r.Text, ["link"] = r.Link, ["en"] = r.En,
        ["buttons"] = new JsonArray((r.Buttons ?? Array.Empty<ChatButton>()).Select(b => (JsonNode)new JsonObject
        {
            ["label"] = b.Label, ["action"] = b.Action, ["value"] = b.Value, ["style"] = b.Style, ["url"] = b.Url,
        }).ToArray()),
    };

    public static ChatReply Deserialize(JsonElement o)
    {
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var buttons = o.TryGetProperty("buttons", out var bs) && bs.ValueKind == JsonValueKind.Array
            ? bs.EnumerateArray().Select(b => new ChatButton(S(b, "label") ?? "", S(b, "action") ?? "open", S(b, "value") ?? "", S(b, "style"), S(b, "url"))).ToList()
            : new List<ChatButton>();
        return new ChatReply(S(o, "text") ?? "", Array.Empty<PendingApproval>())
        {
            Link = S(o, "link"), En = o.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.True, Buttons = buttons.Count == 0 ? null : buttons,
        };
    }

    // ------------------------------------------------------------------ Mattermost / Rocket.Chat kimlikleri

    public async Task<ChatIdentity> EnsureGenericIdentityAsync(GovernanceDbContext db, ChatApp app, string externalUserId, string? dmChannelId, CancellationToken ct)
    {
        var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.ExternalUserId == externalUserId, ct);
        if (id is null)
        {
            id = new ChatIdentity { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = app.Platform, ExternalUserId = externalUserId };
            db.ChatIdentities.Add(id);
        }
        if (id.Email is null)
        {
            try
            {
                var token = SecretBox.Unprotect(app.BotTokenEnc)!;
                if (app.Platform == "Mattermost")
                    (id.Email, id.DisplayName) = await _mm.UserAsync(app.ServerUrl!, token, externalUserId, ct);
                else
                {
                    var (email, name, _) = await _rc.UserAsync(app.ServerUrl!, app.BotUserId!, token, externalUserId, ct);
                    (id.Email, id.DisplayName) = (email, name);
                }
            }
            catch (ChatApiException ex) { _log.LogInformation("{Platform} kullanıcı bilgisi alınamadı: {Message}", app.Platform, ex.Message); }
        }
        if (dmChannelId is not null) id.ConversationId = dmChannelId;
        if (id.EmployeeId is null && id.Email is not null)
            id.EmployeeId = await _chat.EmployeeIdByEmailAsync(app.TenantSlug, id.Email, ct);
        id.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>Mattermost/Rocket.Chat için çalışanın sohbet hesabını e-postayla bulur (onay kartı gönderirken).</summary>
    public async Task<ChatIdentity?> FindGenericByEmployeeAsync(GovernanceDbContext db, ChatApp app, Guid employeeId, CancellationToken ct)
    {
        var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.EmployeeId == employeeId, ct);
        if (id is not null) return id;
        var email = await _sql.ScalarAsync("SELECT \"Email\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, app.TenantSlug, employeeId) as string;
        if (string.IsNullOrWhiteSpace(email)) return null;
        var token = SecretBox.Unprotect(app.BotTokenEnc)!;
        var userId = app.Platform == "Mattermost"
            ? await _mm.UserIdByEmailAsync(app.ServerUrl!, token, email, ct)
            : (await _rc.UserByEmailAsync(app.ServerUrl!, app.BotUserId!, token, email, ct)).Id;
        if (userId is null) return null;
        id = new ChatIdentity { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = app.Platform, ExternalUserId = userId, Email = email, EmployeeId = employeeId };
        db.ChatIdentities.Add(id);
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>Mattermost/Rocket.Chat onay kartı (Slack/Teams'tekinin karşılığı).</summary>
    public async Task SendApprovalGenericAsync(GovernanceDbContext db, ChatApp app, Guid approverId, PendingApproval p, bool en, CancellationToken ct)
    {
        var id = await FindGenericByEmployeeAsync(db, app, approverId, ct);
        if (id is null) return;
        if (id.ConversationId is null && ChatService.Trusted(app, id))
        {
            // DM kanalı yoksa açılır.
            await IdentityForEmployeeAsync(db, app, approverId, ct);
        }
        if (!ChatService.Trusted(app, id))
        {
            if (id.ConversationId is null)
            {
                var token = SecretBox.Unprotect(app.BotTokenEnc)!;
                id.ConversationId = app.Platform == "Mattermost"
                    ? await _mm.DirectChannelAsync(app.ServerUrl!, token, app.BotUserId!, id.ExternalUserId, ct)
                    : (await _rc.UserAsync(app.ServerUrl!, app.BotUserId!, token, id.ExternalUserId, ct)).Username is { } un
                        ? await _rc.DirectRoomAsync(app.ServerUrl!, app.BotUserId!, token, un, ct) : null;
            }
            if (id.ConversationId is null) return;
            var prompt = ChatService.LinkPrompt(await _chat.LinkUrlAsync(db, id, TimeSpan.FromHours(24), ct), forApproval: true, en);
            await PostAsync(app, id.ConversationId, null, ChatReply.Of(prompt, en), ct);
            return;
        }
        var reply = ChatReply.Of(ChatCards.ApprovalText(p, en), en, ChatCards.ApprovalButtons(p, en).ToArray());
        var msgId = await PostAsync(app, id.ConversationId!, null, reply, ct);
        db.ChatMessages.Add(new ChatMessage { TenantSlug = app.TenantSlug, AppId = app.Id, Platform = app.Platform, WorkflowRequestId = p.WorkflowId,
            StepId = p.StepId, RecipientEmployeeId = approverId, ConversationId = id.ConversationId!, MessageId = msgId ?? "", Subject = p.Subject });
        app.LastActivityAt = DateTime.UtcNow;
        app.LastError = null;
    }

    /// <summary>Mattermost/Rocket.Chat onay kartını günceller (düğmeler kalkar).</summary>
    public async Task UpdateGenericAsync(ChatApp app, ChatMessage m, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(m.MessageId)) return;
        var token = SecretBox.Unprotect(app.BotTokenEnc)!;
        if (app.Platform == "Mattermost") await _mm.UpdateAsync(app.ServerUrl!, token, m.MessageId, ChatCards.MarkdownFor("Mattermost", text), ct);
        else await _rc.UpdateAsync(app.ServerUrl!, app.BotUserId!, token, m.ConversationId, m.MessageId, text, ct);
    }

    // ------------------------------------------------------------------ BG16 konuşma bağlamı

    public async Task<List<(string Role, string Text, DateTime At)>> ContextAsync(string tenant, Guid employeeId, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync("""
            SELECT "Role", "TextEnc", "CreatedAt" FROM governance_chat_context
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "CreatedAt" > now() - make_interval(days => $3)
            ORDER BY "CreatedAt" DESC LIMIT $4
            """, r => (Role: r.GetString(0), Enc: r.GetString(1), At: r.GetFieldValue<DateTime>(2)), ct, tenant, employeeId, ContextDays, ContextTurns * 2);
        return rows.Select(r => (r.Role, SecretBox.Unprotect(r.Enc) ?? "", r.At)).Reverse().ToList();
    }

    public async Task RememberAsync(string tenant, Guid employeeId, string question, string answer, CancellationToken ct)
    {
        try
        {
            await _sql.ExecuteAsync("""
                INSERT INTO governance_chat_context ("Id","TenantSlug","EmployeeId","Role","TextEnc","CreatedAt") VALUES ($1,$2,$3,'user',$4,now()), ($5,$2,$3,'bot',$6,now() + interval '1 millisecond')
                """, ct, Guid.NewGuid(), tenant, employeeId, SecretBox.Protect(Trim(question, 500))!, Guid.NewGuid(), SecretBox.Protect(Trim(answer, 500))!);
            // Yalnızca son N tur tutulur.
            await _sql.ExecuteAsync("""
                DELETE FROM governance_chat_context WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Id" NOT IN
                  (SELECT "Id" FROM governance_chat_context WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" DESC LIMIT $3)
                """, ct, tenant, employeeId, ContextTurns * 2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Bağlam yazılamadı"); }
    }

    public Task<int> ForgetAsync(string tenant, Guid employeeId, CancellationToken ct) =>
        _sql.ExecuteAsync("DELETE FROM governance_chat_context WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, tenant, employeeId);

    // ------------------------------------------------------------------ BG18 anlaşılamayan sorular

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime At)> Misses = new();

    /// <summary>Art arda anlaşılamayan soru sayısı (10 dk içinde); başarılı yanıtta sıfırlanır.</summary>
    public static int Miss(Guid appId, string user, bool missed)
    {
        var key = $"{appId}:{user}";
        if (!missed) { Misses.TryRemove(key, out _); return 0; }
        var now = DateTime.UtcNow;
        var v = Misses.AddOrUpdate(key, _ => (1, now), (_, old) => now - old.At > TimeSpan.FromMinutes(10) ? (1, now) : (old.Count + 1, now));
        return v.Count;
    }

    // ------------------------------------------------------------------ BG7 yardım

    public static ChatReply HelpReply(bool en) => ChatReply.Of(ChatService.HelpOf(en), en,
        ChatButton.Say(en ? "Approvals" : "Onaylarım", en ? "approvals" : "onaylarım"),
        ChatButton.Say(en ? "Balance" : "Bakiye", en ? "balance" : "bakiye"),
        ChatButton.Say(en ? "Request leave" : "İzin al", en ? "request leave" : "izin al"),
        ChatButton.Say(en ? "My shifts" : "Vardiyam", en ? "myshifts" : "vardiyam"),
        ChatButton.Say(en ? "Book a desk" : "Masa ayır", en ? "desk" : "masa"),
        ChatButton.Say(en ? "Announcements" : "Duyurular", en ? "announcements" : "duyurular"),
        ChatButton.Say(en ? "Summary" : "Özet", en ? "summary" : "özet"));

    /// <summary>Komutun okunur adı (yazım hatası düzeltilince kullanıcıya söylenir).</summary>
    public static string DisplayName(string cmd, bool en) => (cmd, en) switch
    {
        ("approvals", false) => "onaylarım", ("approvals", true) => "approvals",
        ("balance", false) => "bakiye", ("balance", true) => "balance",
        ("leave", false) => "izin al", ("leave", true) => "request leave",
        ("home", false) => "özet", ("home", true) => "summary",
        ("onleave", false) => "izindekiler", ("onleave", true) => "on leave",
        ("shifts", false) => "vardiyam", ("shifts", true) => "my shifts",
        ("swaps", false) => "takas", ("swaps", true) => "swaps",
        ("desk", false) => "masa", ("desk", true) => "desk",
        ("announcements", false) => "duyurular", ("announcements", true) => "announcements",
        ("clockin", false) => "geldim", ("clockout", false) => "çıktım",
        ("cancelleave", false) => "izin iptal", ("kudos", false) => "teşekkür",
        ("expense", false) => "masraf", ("documents", false) => "belge",
        _ => cmd,
    };
}
