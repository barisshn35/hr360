using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;
using GovernanceService.Tenancy;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Dalga 5e — B7 Mattermost ve Rocket.Chat uçları, BG13 adım doğrulaması,
 * BG20 bot yönetimi (komut aç/kapat, kullanım sayıları, son hatalar),
 * B12 nabız planı, B11 yıldönümü izni.
 * ==================================================================== */

/// <summary>Mattermost: slash komutu, giden webhook ve etkileşimli düğmeler. Kimlik doğrulama: paylaşılan jeton.</summary>
[ApiController]
[Route("api/chat/mattermost/{appId:guid}")]
[AllowAnonymous]
[BufferBody]
public class MattermostEndpointsController : ControllerBase
{
    private readonly GovernanceDbContext _db;
    private readonly ChatService _chat;
    private readonly TenantContext _tenant;
    private readonly ILogger<MattermostEndpointsController> _log;

    public MattermostEndpointsController(GovernanceDbContext db, ChatService chat, TenantContext tenant, ILogger<MattermostEndpointsController> log)
    { _db = db; _chat = chat; _tenant = tenant; _log = log; }

    /// <summary>Gövde form ya da JSON olabilir (Mattermost giden webhook içerik türü ayarı).</summary>
    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        Request.Body.Position = 0;
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, false, 4096, leaveOpen: true)) raw = await reader.ReadToEndAsync(ct);
        var d = new Dictionary<string, string>();
        if (raw.TrimStart().StartsWith('{'))
        {
            try
            {
                foreach (var p in JsonDocument.Parse(raw).RootElement.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }
            catch (JsonException) { }
        }
        else
            foreach (var kv in Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(raw)) d[kv.Key] = kv.Value.ToString();
        return d;
    }

    private async Task<ChatApp?> AppAsync(Guid appId, string? token, CancellationToken ct)
    {
        var app = await _db.ChatApps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId && a.Platform == "Mattermost" && a.IsEnabled, ct);
        if (app is null || !IncomingToken.Matches(SecretBox.Unprotect(app.IncomingTokenEnc), token)) return null;
        if (!ChatHosts.Allowed(app, await TransferGuard.AllowedAsync(_db, app.TenantSlug, ct))) return null;
        _tenant.TenantSlug = app.TenantSlug;
        return app;
    }

    private static object Response(ChatApp app, ChatReply reply, bool ephemeral = true) => new
    {
        response_type = ephemeral ? "ephemeral" : "in_channel",
        text = ChatFeatures.MmText(reply, "Mattermost"),
        props = new { attachments = ChatCards.MattermostAttachments(app, "", ChatFeatures.AllButtons(reply), DateTimeOffset.UtcNow) },
    };

    /// <summary>Slash komutu (/hr360 ...): yanıt yalnızca komutu yazana görünür (ephemeral).</summary>
    [HttpPost("commands")]
    public async Task<IActionResult> Command(Guid appId, CancellationToken ct)
    {
        var f = await ReadAsync(ct);
        var app = await AppAsync(appId, f.GetValueOrDefault("token"), ct);
        if (app is null) return Unauthorized();
        if (!f.TryGetValue("user_id", out var userId) || string.IsNullOrEmpty(userId)) return BadRequest();
        var who = await _chat.Features.EnsureGenericIdentityAsync(_db, app, userId, null, ct);
        var reply = await _chat.CommandAsync(_db, app, who, f.GetValueOrDefault("text"), ct, HttpContext.RequestServices.GetRequiredService<HrAssistant>());
        await _db.SaveChangesAsync(ct);
        return Ok(Response(app, reply));
    }

    /// <summary>
    /// Giden webhook (kanaldaki tetik kelimesi). Kanal herkese açık olabileceğinden kişisel yanıt kanala
    /// yazılmaz: kişiye DM gider, kanala yalnızca yönlendirme.
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(Guid appId, CancellationToken ct)
    {
        var f = await ReadAsync(ct);
        var app = await AppAsync(appId, f.GetValueOrDefault("token"), ct);
        if (app is null) return Unauthorized();
        if (!f.TryGetValue("user_id", out var userId) || string.IsNullOrEmpty(userId) || userId == app.BotUserId) return Ok(new { });
        var text = f.GetValueOrDefault("text") ?? "";
        if (f.GetValueOrDefault("trigger_word") is { Length: > 0 } tw && text.StartsWith(tw, StringComparison.OrdinalIgnoreCase)) text = text[tw.Length..].Trim();
        var who = await _chat.Features.EnsureGenericIdentityAsync(_db, app, userId, null, ct);
        var reply = await _chat.CommandAsync(_db, app, who, text, ct, HttpContext.RequestServices.GetRequiredService<HrAssistant>());
        bool dm;
        try { dm = who.EmployeeId is not null && await _chat.Features.DeliverAsync(_db, app, who.EmployeeId.Value, reply, ct); }
        catch (ChatApiException ex) { _log.LogInformation("Mattermost DM gönderilemedi: {Message}", ex.Message); dm = false; }
        await _db.SaveChangesAsync(ct);
        var en = reply.En;
        return Ok(new
        {
            text = dm ? (en ? "I sent the answer to you in a direct message." : "Yanıtı size özel mesajla gönderdim.")
                : (en ? "Personal information is not shared in channels. Please message me directly or use the /hr360 command." : "Kişisel bilgiler kanalda paylaşılmaz. Lütfen bana özel mesaj yazın ya da /hr360 komutunu kullanın."),
        });
    }

    /// <summary>Etkileşimli düğme: bağlam imzası (jetondan türetilen HMAC) doğrulanır; karar yine sunucuda yetkilendirilir.</summary>
    [HttpPost("actions")]
    public async Task<IActionResult> Action(Guid appId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var app = await _db.ChatApps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId && a.Platform == "Mattermost" && a.IsEnabled, ct);
        if (app is null) return Unauthorized();
        string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var context = body.TryGetProperty("context", out var c) ? c : default;
        var action = S(context, "a") ?? "";
        var value = S(context, "v") ?? "";
        var secret = SecretBox.Unprotect(app.IncomingTokenEnc) ?? "";
        if (!IncomingToken.Matches(IncomingToken.Sign(secret, action, value), S(context, "sig"))) return Unauthorized();
        if (!ChatHosts.Allowed(app, await TransferGuard.AllowedAsync(_db, app.TenantSlug, ct))) return Unauthorized();
        _tenant.TenantSlug = app.TenantSlug;
        var userId = S(body, "user_id");
        if (string.IsNullOrEmpty(userId)) return BadRequest();
        var who = await _chat.Features.EnsureGenericIdentityAsync(_db, app, userId, null, ct);
        var reply = await _chat.Features.ActionAsync(_db, app, who, action, value, ct, HttpContext.RequestServices.GetRequiredService<HrAssistant>());
        await _db.SaveChangesAsync(ct);
        if (reply.Replace)
            return Ok(new
            {
                update = new
                {
                    message = ChatFeatures.MmText(reply, "Mattermost"),
                    props = new { attachments = ChatCards.MattermostAttachments(app, "", ChatFeatures.AllButtons(reply), DateTimeOffset.UtcNow) },
                },
            });
        if (S(body, "channel_id") is { } ch)
        {
            try { await _chat.Features.PostAsync(app, ch, null, reply, ct); }
            catch (ChatApiException ex) { return Ok(new { ephemeral_text = reply.Text + " (" + ex.Message + ")" }); }
        }
        return Ok(new { });
    }
}

/// <summary>Rocket.Chat giden entegrasyonu (Outgoing WebHook). Kimlik doğrulama: entegrasyon jetonu.</summary>
[ApiController]
[Route("api/chat/rocketchat/{appId:guid}/webhook")]
[AllowAnonymous]
public class RocketChatEndpointsController : ControllerBase
{
    private readonly GovernanceDbContext _db;
    private readonly ChatService _chat;
    private readonly TenantContext _tenant;
    private readonly ILogger<RocketChatEndpointsController> _log;

    public RocketChatEndpointsController(GovernanceDbContext db, ChatService chat, TenantContext tenant, ILogger<RocketChatEndpointsController> log)
    { _db = db; _chat = chat; _tenant = tenant; _log = log; }

    [HttpPost]
    public async Task<IActionResult> Post(Guid appId, [FromBody] JsonElement body, CancellationToken ct)
    {
        string? S(string n) => body.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var app = await _db.ChatApps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId && a.Platform == "RocketChat" && a.IsEnabled, ct);
        if (app is null || !IncomingToken.Matches(SecretBox.Unprotect(app.IncomingTokenEnc), S("token"))) return Unauthorized();
        if (!ChatHosts.Allowed(app, await TransferGuard.AllowedAsync(_db, app.TenantSlug, ct))) return Unauthorized();
        _tenant.TenantSlug = app.TenantSlug;
        var userId = S("user_id");
        if (string.IsNullOrEmpty(userId) || userId == app.BotUserId || (body.TryGetProperty("bot", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.Object)) return Ok(new { });
        var room = S("channel_id") ?? "";
        // Rocket.Chat DM oda kimliği iki kullanıcı kimliğinin birleşimidir.
        var isDm = app.BotUserId is not null && room.Contains(userId) && room.Contains(app.BotUserId);
        var who = await _chat.Features.EnsureGenericIdentityAsync(_db, app, userId, isDm ? room : null, ct);
        var text = S("text") ?? "";
        if (S("trigger_word") is { Length: > 0 } tw && text.StartsWith(tw, StringComparison.OrdinalIgnoreCase)) text = text[tw.Length..].Trim();
        var hr = HttpContext.RequestServices.GetRequiredService<HrAssistant>();
        var reply = ChatCards.RocketAction(text) is { } act
            ? await _chat.Features.ActionAsync(_db, app, who, act.Action, act.Value, ct, hr)
            : await _chat.CommandAsync(_db, app, who, text, ct, hr);
        await _db.SaveChangesAsync(ct);
        if (isDm)
            return Ok(new { text = ChatFeatures.MmText(reply, "RocketChat"), attachments = ChatCards.RocketAttachments(ChatFeatures.AllButtons(reply), DateTimeOffset.UtcNow) });
        bool dm;
        try { dm = who.EmployeeId is not null && await _chat.Features.DeliverAsync(_db, app, who.EmployeeId.Value, reply, ct); await _db.SaveChangesAsync(ct); }
        catch (ChatApiException ex) { _log.LogInformation("Rocket.Chat DM gönderilemedi: {Message}", ex.Message); dm = false; }
        return Ok(new
        {
            text = dm ? (reply.En ? "I sent the answer to you in a direct message." : "Yanıtı size özel mesajla gönderdim.")
                : (reply.En ? "Personal information is not shared in channels. Please message me directly." : "Kişisel bilgiler kanalda paylaşılmaz. Lütfen bana özel mesaj yazın."),
        });
    }
}

/// <summary>BG13: sohbetteki hassas işlemin HR360'ta (oturum açık) doğrulanması.</summary>
[Route("api/chat/stepup")]
[Authorize]
public class ChatStepUpController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly ChatService _chat;
    public ChatStepUpController(GovernanceDbContext db, ChatService chat) { _db = db; _chat = chat; }

    private static string KindLabel(string kind, bool en) => (kind, en) switch
    {
        ("stepup:payslip", false) => "Bordro özeti", ("stepup:payslip", true) => "Payslip summary",
        ("stepup:bulk", false) => "Toplu onay", ("stepup:bulk", true) => "Bulk approval",
        ("stepup:decide", false) => "Ücretle ilgili onay", ("stepup:decide", true) => "Pay-related approval",
        _ => kind,
    };

    [HttpGet("{code}")]
    public async Task<IActionResult> Preview(string code, CancellationToken ct)
    {
        var p = await _chat.Features.StepUpByCodeAsync(code, ct);
        var me = await MyPersonAsync(ct);
        if (p is null || me is null || p.EmployeeId != me.Id)
            return NotFound(new { message = L("Doğrulama bağlantısı geçersiz, süresi dolmuş ya da başka bir kişiye ait.", "The verification link is invalid, expired or belongs to someone else.") });
        return Ok(new { kind = p.Kind[7..], label = KindLabel(p.Kind, En), summary = p.Summary, expiresAt = p.ExpiresAt });
    }

    public record ConfirmInput(string Code);

    [HttpPost]
    public async Task<IActionResult> Confirm(ConfirmInput body, CancellationToken ct)
    {
        var p = await _chat.Features.StepUpByCodeAsync(body.Code, ct);
        var me = await MyPersonAsync(ct);
        if (p is null || me is null || p.EmployeeId != me.Id)
            return NotFound(new { message = L("Doğrulama bağlantısı geçersiz, süresi dolmuş ya da başka bir kişiye ait.", "The verification link is invalid, expired or belongs to someone else.") });
        var (ok, message) = await _chat.Features.ConfirmStepUpAsync(_db, Tenant, me.Id, p.Id, notify: true, ct);
        await _chat.Features.AuditAsync(Tenant, me.Id, "ChatStepUp", p.Id.ToString(), ok ? "StepUpConfirmed" : "StepUpFailed", new { kind = p.Kind[7..] }, "web");
        // Sonuç sohbete gönderilir; burada içerik (ör. bordro tutarı) gösterilmez.
        return ok ? Ok(new { confirmed = true }) : Conflict(new { message });
    }

    /// <summary>Panelde gösterilen 6 haneli kodlar (sohbete "kod 123456" yazılır).</summary>
    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(Array.Empty<object>());
        var list = await _chat.Features.StepUpOtpsAsync(Tenant, me.Id, ct);
        return Ok(list.Select(x => new { id = x.Id, summary = x.Summary, otp = x.Otp, expiresAt = x.ExpiresAt }));
    }
}

/// <summary>B11: kişinin iş yıldönümü kutlaması izni (doğum günü engagement profilindeki "doğum günümü göster" ayarıdır).</summary>
[Route("api/chat/optins/me")]
[Authorize]
public class ChatOptInController : AppController
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { showAnniversary = false, showBirthday = false, linked = false });
        var ann = await Db.ScalarAsync("SELECT \"ShowAnniversary\" FROM governance_chat_optins WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, Tenant, me.Id) as bool? ?? false;
        var bday = await Db.ScalarAsync("SELECT \"ShowBirthday\" FROM engagement_profiles WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, Tenant, me.Id) as bool? ?? false;
        return Ok(new { showAnniversary = ann, showBirthday = bday, linked = true });
    }

    public record OptInInput(bool ShowAnniversary);

    [HttpPut]
    public async Task<IActionResult> Put(OptInInput body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = L("Hesabınıza bağlı çalışan kaydı yok.", "Your account is not linked to an employee record.") });
        await Db.ExecuteAsync("""
            INSERT INTO governance_chat_optins ("TenantSlug","EmployeeId","ShowAnniversary","UpdatedAt") VALUES ($1,$2,$3,now())
            ON CONFLICT ("TenantSlug","EmployeeId") DO UPDATE SET "ShowAnniversary" = $3, "UpdatedAt" = now()
            """, ct, Tenant, me.Id, body.ShowAnniversary);
        return Ok(new { showAnniversary = body.ShowAnniversary });
    }
}

/// <summary>BG20 bot yönetimi + B12 nabız planı (kiracı yöneticisi / İK).</summary>
[Route("api/chat-admin")]
[Authorize(Policy = "RequireHrAdmin")]
public class ChatBotAdminController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly ChatService _chat;
    public ChatBotAdminController(GovernanceDbContext db, ChatService chat) { _db = db; _chat = chat; }

    [HttpGet("features")]
    public IActionResult Features() => Ok(ChatFeatureCatalog.All.Select(f => new { key = f.Key, label = En ? f.LabelEn : f.Label }));

    public record FeaturesInput(List<string> Disabled);

    /// <summary>Uygulamada kapatılan komut/özellikler (kapalı komut "bu komut şirketinizde kapalı" yanıtını verir).</summary>
    [HttpPut("apps/{id:guid}/features")]
    public async Task<IActionResult> SetFeatures(Guid id, FeaturesInput body, CancellationToken ct)
    {
        var a = await _db.ChatApps.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        var unknown = (body.Disabled ?? new()).Where(k => !ChatFeatureCatalog.Keys.Contains(k)).ToList();
        if (unknown.Count > 0) return BadRequest(new { message = L($"Bilinmeyen özellik: {string.Join(", ", unknown)}", $"Unknown feature: {string.Join(", ", unknown)}") });
        a.DisabledFeatures = (body.Disabled ?? new()).Distinct().OrderBy(x => x).ToList();
        await _db.SaveChangesAsync(ct);
        return Ok(new { disabled = a.DisabledFeatures });
    }

    /// <summary>Kullanım sayıları (yalnızca sayı; kişi ya da mesaj yok) ve son hatalar.</summary>
    [HttpGet("apps/{id:guid}/stats")]
    public async Task<IActionResult> Stats(Guid id, [FromQuery] int days = 30, CancellationToken ct = default)
    {
        var a = await _db.ChatApps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        days = Math.Clamp(days, 1, 365);
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(-days + 1);
        var rows = await Db.QueryAsync("""
            SELECT "Feature", "Outcome", sum("Count")::int FROM governance_chat_usage WHERE "TenantSlug" = $1 AND "AppId" = $2 AND "Day" >= $3 GROUP BY 1, 2 ORDER BY 3 DESC
            """, r => new { feature = r.GetString(0), outcome = r.GetString(1), count = r.GetInt32(2) }, ct, Tenant, id, since);
        var daily = await Db.QueryAsync("""
            SELECT "Day", sum("Count")::int FROM governance_chat_usage WHERE "TenantSlug" = $1 AND "AppId" = $2 AND "Day" >= $3 GROUP BY 1 ORDER BY 1
            """, r => new { day = r.GetFieldValue<DateOnly>(0), count = r.GetInt32(1) }, ct, Tenant, id, since);
        var errors = await Db.QueryAsync("""
            SELECT "At", "Feature", "Message" FROM governance_chat_errors WHERE "TenantSlug" = $1 AND "AppId" = $2 ORDER BY "At" DESC LIMIT 10
            """, r => new { at = r.GetFieldValue<DateTime>(0), feature = r.GetString(1), message = r.GetString(2) }, ct, Tenant, id);
        var users = await _db.ChatIdentities.AsNoTracking().CountAsync(i => i.AppId == id && i.EmployeeId != null && i.VerifiedAt != null, ct);
        return Ok(new { days, total = rows.Sum(r => r.count), linkedUsers = users, byFeature = rows, daily, errors, lastError = a.LastError });
    }

    /// <summary>Zamanlanmış bot işlerini hemen çalıştırır (deneme / test).</summary>
    [HttpPost("jobs/run")]
    public async Task<IActionResult> RunJobs(CancellationToken ct) =>
        Ok(await _chat.Features.RunAllJobsAsync(_db, Tenant, force: true, ct));

    [HttpGet("pulses")]
    public async Task<IActionResult> Pulses(CancellationToken ct) => Ok(await _chat.Features.PulsesAsync(Tenant, ct));

    public record PulseInput(string Question, DateTime? SendAt, DateTime? ClosesAt);

    /// <summary>Tek soruluk (1–5) anonim nabız: bot, eşleşmiş çalışanlara düğmeli DM gönderir.</summary>
    [HttpPost("pulses")]
    public async Task<IActionResult> CreatePulse(PulseInput body, CancellationToken ct)
    {
        var q = (body.Question ?? "").Trim();
        if (q.Length is < 5 or > 300) return BadRequest(new { message = L("Soru 5–300 karakter olmalı.", "The question must be 5–300 characters.") });
        var send = body.SendAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : DateTime.UtcNow;
        var close = body.ClosesAt is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : send.AddDays(7);
        if (close <= send) return BadRequest(new { message = L("Kapanış zamanı gönderimden sonra olmalı.", "The closing time must be after the send time.") });
        var (id, status, error) = await _chat.Features.SchedulePulseAsync(Tenant, q, send, close, Me.Name, ct);
        if (id is null) return StatusCode(status is >= 400 and < 500 and not 404 ? status : StatusCodes.Status502BadGateway, new { message = error });
        return Ok(new { id });
    }

    [HttpPost("pulses/{id:guid}/close")]
    public async Task<IActionResult> ClosePulse(Guid id, CancellationToken ct)
    {
        var survey = await Db.ScalarAsync("UPDATE governance_chat_pulses SET \"Status\" = 'Closed', \"ClosesAt\" = now() WHERE \"TenantSlug\" = $1 AND \"Id\" = $2 RETURNING \"SurveyId\"", ct, Tenant, id);
        if (survey is not Guid sid) return NotFound();
        // Anket engagement-service'te kapatılır; başarısızsa İK tekrar deneyebilir (yukarıdaki güncelleme tekrarlanabilir).
        if (!await _chat.Features.ClosePulseSurveyAsync(Tenant, sid, ct))
            return StatusCode(StatusCodes.Status502BadGateway, new { message = L("Anket kapatılamadı; lütfen tekrar deneyin.", "The survey could not be closed; please try again.") });
        return NoContent();
    }
}
