using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Calendar;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;
using GovernanceService.Tenancy;

namespace GovernanceService.Controllers;

/* ======================================================================
 * Takvim ve toplantı sağlayıcıları (yönetici): Google, Microsoft 365, Zoom.
 * ==================================================================== */
[Route("api/calendar/providers")]
[Authorize(Policy = "RequireHrAdmin")]
public class ProviderConfigsController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly ZoomApi _zoom;
    public ProviderConfigsController(GovernanceDbContext db, ZoomApi zoom) { _db = db; _zoom = zoom; }

    private static readonly string[] Known = ["Google", "Microsoft", "Zoom"];

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var cfgs = await _db.ProviderConfigs.AsNoTracking().ToListAsync(ct);
        var counts = await _db.CalendarConnections.AsNoTracking().GroupBy(c => c.Provider).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return Ok(Known.Select(p =>
        {
            var c = cfgs.FirstOrDefault(x => x.Provider == p);
            return new
            {
                provider = p, configured = c is not null, isEnabled = c?.IsEnabled ?? false, clientId = c?.ClientId, hasSecret = c?.ClientSecretEnc != null,
                msTenant = c?.MsTenant, zoomAccountId = c?.ZoomAccountId, zoomDefaultHost = c?.ZoomDefaultHost, lastError = c?.LastError,
                connections = counts.FirstOrDefault(x => x.Key == p)?.N ?? 0,
                redirectUri = p == "Zoom" ? null : CalendarService.RedirectUri(p),
                scopes = p switch { "Google" => GoogleCalendar.Scopes, "Microsoft" => MicrosoftCalendar.Scopes, _ => "meeting:write:meeting:admin, meeting:delete:meeting:admin, user:read:user:admin" },
                publicOriginIsHttps = ChatService.PublicOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            };
        }));
    }

    public record ProviderInput(string ClientId, string? ClientSecret, string? MsTenant, string? ZoomAccountId, string? ZoomDefaultHost, bool IsEnabled);

    [HttpPut("{provider}")]
    public async Task<IActionResult> Save(string provider, ProviderInput body, CancellationToken ct)
    {
        provider = Known.FirstOrDefault(k => k.Equals(provider, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (provider == "") return NotFound();
        if (string.IsNullOrWhiteSpace(body.ClientId)) return BadRequest(new { message = "İstemci kimliği (client ID) zorunlu." });
        if (provider == "Microsoft" && !Guid.TryParse(body.ClientId, out _)) return BadRequest(new { message = "Microsoft uygulama (istemci) kimliği bir GUID olmalı." });
        if (provider == "Microsoft" && !string.IsNullOrWhiteSpace(body.MsTenant) && !Guid.TryParse(body.MsTenant, out _) && body.MsTenant is not ("organizations" or "common"))
            return BadRequest(new { message = "Dizin (kiracı) kimliği bir GUID ya da \"organizations\" olmalı." });
        if (provider == "Zoom" && string.IsNullOrWhiteSpace(body.ZoomAccountId)) return BadRequest(new { message = "Zoom hesap kimliği (Account ID) zorunlu." });
        if (body.IsEnabled && await TransferGuard.MissingAsync(_db, Tenant, TransferGuard.KeyOf(provider), ct) is { } transferError)
            return BadRequest(new { message = transferError, code = "kvkk_transfer" });
        var c = await _db.ProviderConfigs.FirstOrDefaultAsync(x => x.Provider == provider, ct);
        var creating = c is null;
        c ??= new ProviderConfig { Provider = provider };
        if (creating && string.IsNullOrWhiteSpace(body.ClientSecret)) return BadRequest(new { message = "İstemci gizli anahtarı (client secret) zorunlu." });
        c.ClientId = body.ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(body.ClientSecret)) c.ClientSecretEnc = SecretBox.Protect(body.ClientSecret.Trim());
        c.MsTenant = string.IsNullOrWhiteSpace(body.MsTenant) ? null : body.MsTenant.Trim();
        c.ZoomAccountId = body.ZoomAccountId?.Trim();
        c.ZoomDefaultHost = string.IsNullOrWhiteSpace(body.ZoomDefaultHost) ? null : body.ZoomDefaultHost.Trim();
        c.IsEnabled = body.IsEnabled;
        if (provider == "Zoom" && c.IsEnabled)
        {
            // Zoom bilgileri hemen doğrulanabilir (hesap düzeyinde jeton).
            try { await _zoom.TokenAsync(c, ct); c.LastError = null; }
            catch (ProviderApiException ex) { return BadRequest(new { message = ex.Message }); }
            catch (HttpRequestException ex) { return BadRequest(new { message = "Zoom'a ulaşılamadı: " + ex.Message }); }
        }
        if (creating) _db.ProviderConfigs.Add(c);
        await _db.SaveChangesAsync(ct);
        return Ok(new { c.Provider, c.IsEnabled });
    }

    /// <summary>
    /// Dalga 12 (madde 91): bağlantı testi (scripts/integration-check.sh de kullanır). Zoom: hesap jetonu alınır.
    /// Google/Microsoft (kullanıcı adına OAuth): gerçek bir yetki kodu olmadan jeton alınamaz; geçersiz bir kodla
    /// jeton ucu çağrılır — "invalid_grant" yanıtı istemci kimliği/gizli anahtarın doğru olduğunu, "invalid_client"
    /// ya da 401 yanlış olduğunu gösterir. Sır yanıtta ve günlükte yer almaz.
    /// </summary>
    [HttpPost("{provider}/test")]
    public async Task<IActionResult> Test(string provider, [FromServices] IHttpClientFactory http, CancellationToken ct)
    {
        provider = Known.FirstOrDefault(k => k.Equals(provider, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (provider == "") return NotFound();
        var c = await _db.ProviderConfigs.FirstOrDefaultAsync(x => x.Provider == provider, ct);
        if (c is null) return NotFound(new { message = "Sağlayıcı yapılandırılmamış." });
        string? error = null;
        try
        {
            if (provider == "Zoom") await _zoom.TokenAsync(c, ct);
            else
            {
                var secret = SecretBox.Unprotect(c.ClientSecretEnc) ?? "";
                var url = provider == "Google"
                    ? EnvVar.Or("GOOGLE_OAUTH_BASE", "https://oauth2.googleapis.com").TrimEnd('/') + "/token"
                    : $"{EnvVar.Or("MS_LOGIN_BASE", "https://login.microsoftonline.com").TrimEnd('/')}/{Uri.EscapeDataString(string.IsNullOrWhiteSpace(c.MsTenant) ? "organizations" : c.MsTenant)}/oauth2/v2.0/token";
                var client = http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var res = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = c.ClientId, ["client_secret"] = secret, ["grant_type"] = "authorization_code",
                    ["code"] = "hr360-connection-test", ["code_verifier"] = new string('x', 43), ["redirect_uri"] = CalendarService.RedirectUri(provider),
                }), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if ((int)res.StatusCode == 401 || text.Contains("invalid_client", StringComparison.Ordinal) || text.Contains("unauthorized_client", StringComparison.Ordinal))
                    error = "İstemci kimliği ya da gizli anahtar sağlayıcı tarafından reddedildi.";
            }
        }
        catch (ProviderApiException ex) { error = ex.Message; }
        catch (HttpRequestException ex) { error = "Sağlayıcıya ulaşılamadı: " + ex.Message; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = "Sağlayıcı zamanında yanıt vermedi."; }
        c.LastError = error is null ? null : error.Length > 500 ? error[..500] : error;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = error is null, message = error ?? "Bağlantı başarılı.", enabled = c.IsEnabled });
    }

    [HttpDelete("{provider}")]
    public async Task<IActionResult> Delete(string provider, CancellationToken ct)
    {
        var c = await _db.ProviderConfigs.FirstOrDefaultAsync(x => x.Provider.ToLower() == provider.ToLower(), ct);
        if (c is null) return NotFound();
        _db.ProviderConfigs.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/* ======================================================================
 * Kişisel takvim bağlantısı (Google Takvim / Outlook): OAuth 2.0 + PKCE.
 * ==================================================================== */
[Route("api/calendar")]
[Authorize]
public class CalendarConnectionsController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly CalendarService _cal;
    private readonly TenantContext _tenant;
    private readonly ILogger<CalendarConnectionsController> _log;

    public CalendarConnectionsController(GovernanceDbContext db, CalendarService cal, TenantContext tenant, ILogger<CalendarConnectionsController> log)
    { _db = db; _cal = cal; _tenant = tenant; _log = log; }

    [HttpGet("connections")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var available = await _db.ProviderConfigs.AsNoTracking().Where(c => c.IsEnabled && c.Provider != "Zoom").Select(c => c.Provider).ToListAsync(ct);
        var mine = me is null ? new() : await _db.CalendarConnections.AsNoTracking().Where(c => c.EmployeeId == me.Id).ToListAsync(ct);
        return Ok(new
        {
            available, linked = me is not null,
            connections = mine.Select(c => new { c.Id, c.Provider, c.AccountEmail, c.SyncLeaves, c.Status, c.LastError, c.LastSyncAt, c.CreatedAt }),
        });
    }

    [HttpPost("connect/{provider}")]
    public async Task<IActionResult> Connect(string provider, CancellationToken ct)
    {
        provider = provider.Equals("google", StringComparison.OrdinalIgnoreCase) ? "Google" : provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase) ? "Microsoft" : "";
        if (provider == "") return NotFound();
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Hesabınıza bağlı çalışan kaydı yok." });
        var cfg = await CalendarService.ConfigAsync(_db, Tenant, provider, ct);
        if (cfg is null) return BadRequest(new { message = $"{(provider == "Google" ? "Google" : "Microsoft 365")} entegrasyonu yönetici tarafından açılmamış." });
        if (await TransferGuard.MissingAsync(_db, Tenant, TransferGuard.KeyOf(provider), ct) is { } transferError)
            return BadRequest(new { message = transferError, code = "kvkk_transfer" });
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        await _db.OAuthStates.Where(s => s.ExpiresAt < DateTime.UtcNow).ExecuteDeleteAsync(ct);
        _db.OAuthStates.Add(new OAuthState { State = state, EmployeeId = me.Id, UserId = Me.UserId, Provider = provider, CodeVerifier = verifier, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await _db.SaveChangesAsync(ct);
        return Ok(new { authorizeUrl = _cal.Provider(provider).AuthorizeUrl(cfg, state, Pkce.Challenge(verifier), CalendarService.RedirectUri(provider)) });
    }

    /// <summary>Google/Microsoft'un yönlendirdiği adres. Oturum yerine tek kullanımlık "state" ile doğrulanır.</summary>
    [HttpGet("oauth/callback/{provider}")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(string provider, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription, CancellationToken ct)
    {
        IActionResult Back(bool ok, string? message = null) =>
            Redirect($"{ChatService.PublicOrigin}/panel/profil?sekme=takvim&takvim={(ok ? "baglandi" : "hata")}{(message is null ? "" : "&mesaj=" + Uri.EscapeDataString(message[..Math.Min(message.Length, 200)]))}");

        if (string.IsNullOrEmpty(state)) return Back(false, "Geçersiz yanıt.");
        var s = await _db.OAuthStates.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.State == state, ct);
        if (s is null || s.ExpiresAt < DateTime.UtcNow || !s.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            return Back(false, "Bağlantı isteğinin süresi doldu; tekrar deneyin.");
        _db.OAuthStates.Remove(s);
        await _db.SaveChangesAsync(ct);
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Back(false, error == "access_denied" ? "İzin verilmedi." : errorDescription ?? error ?? "Yetkilendirme tamamlanmadı.");

        _tenant.TenantSlug = s.TenantSlug;
        var cfg = await CalendarService.ConfigAsync(_db, s.TenantSlug, s.Provider, ct);
        if (cfg is null) return Back(false, "Entegrasyon kapatılmış.");
        try
        {
            var p = _cal.Provider(s.Provider);
            var t = await p.ExchangeCodeAsync(cfg, code, s.CodeVerifier, CalendarService.RedirectUri(s.Provider), ct);
            if (t.RefreshToken is null) return Back(false, "Sağlayıcı yenileme jetonu vermedi; erişimi kaldırıp tekrar bağlayın.");
            var email = await p.AccountEmailAsync(t.AccessToken, ct);
            var conn = await _db.CalendarConnections.FirstOrDefaultAsync(c => c.EmployeeId == s.EmployeeId && c.Provider == s.Provider, ct);
            if (conn is null)
            {
                conn = new CalendarConnection { EmployeeId = s.EmployeeId, Provider = s.Provider };
                _db.CalendarConnections.Add(conn);
            }
            conn.UserId = s.UserId;
            conn.AccountEmail = email;
            conn.AccessTokenEnc = SecretBox.Protect(t.AccessToken);
            conn.RefreshTokenEnc = SecretBox.Protect(t.RefreshToken);
            conn.ExpiresAt = DateTime.UtcNow.AddSeconds(t.ExpiresIn);
            conn.Status = "Active";
            conn.LastError = null;
            await _db.SaveChangesAsync(ct);
            await BackfillLeavesAsync(conn, ct);
            return Back(true);
        }
        catch (Exception ex) when (ex is ProviderApiException or CalendarFlowException or HttpRequestException)
        {
            _log.LogInformation("Takvim bağlanamadı: {Message}", ex.Message);
            return Back(false, ex.Message);
        }
    }

    /// <summary>Bağlantı kurulunca gelecekteki onaylı izinler takvime yazılır.</summary>
    private async Task BackfillLeavesAsync(CalendarConnection conn, CancellationToken ct)
    {
        if (!conn.SyncLeaves) return;
        var ids = await Db.QueryAsync("""
            SELECT "Id" FROM leave_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Status" = 'Approved' AND "EndDate" >= CURRENT_DATE
            ORDER BY "StartDate" LIMIT 50
            """, r => r.GetGuid(0), ct, conn.TenantSlug, conn.EmployeeId);
        foreach (var id in ids)
            await _cal.OnEventAsync(_db, conn.TenantSlug, "leave.approved",
                System.Text.Json.JsonSerializer.SerializeToElement(new { LeaveRequestId = id, EmployeeId = conn.EmployeeId }), ct);
    }

    public record ConnectionPatch(bool SyncLeaves);

    [HttpPatch("connections/{id:guid}")]
    public async Task<IActionResult> Patch(Guid id, ConnectionPatch body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var c = await _db.CalendarConnections.FirstOrDefaultAsync(x => x.Id == id && me != null && x.EmployeeId == me.Id, ct);
        if (c is null) return NotFound();
        var turnedOn = body.SyncLeaves && !c.SyncLeaves;
        c.SyncLeaves = body.SyncLeaves;
        await _db.SaveChangesAsync(ct);
        if (turnedOn) await BackfillLeavesAsync(c, ct);
        return Ok(new { c.Id, c.SyncLeaves });
    }

    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var c = await _db.CalendarConnections.FirstOrDefaultAsync(x => x.Id == id && me != null && x.EmployeeId == me.Id, ct);
        if (c is null) return NotFound();
        _db.CalendarConnections.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/* ======================================================================
 * Toplantılar: 1:1, mülakat ya da serbest toplantı için Zoom / Teams /
 * Google Meet bağlantısı ve takvim daveti; boş/dolu ve saat önerisi.
 * ==================================================================== */
[Route("api/meetings")]
[Authorize]
public class MeetingsController : AppController
{
    private readonly GovernanceDbContext _db;
    private readonly CalendarService _cal;
    public MeetingsController(GovernanceDbContext db, CalendarService cal) { _db = db; _cal = cal; }

    private static object View(Meeting m) => new
    {
        m.Id, m.SourceType, m.SourceId, m.Title, m.Description, m.StartsAt, m.DurationMinutes, m.Provider, m.JoinUrl, m.Status,
        m.OrganizerEmployeeId, m.ParticipantEmployeeIds, m.ExternalEmails, warnings = m.Warnings?.Split('\n'), m.CreatedAt,
    };

    /// <summary>Hangi toplantı türleri kullanılabilir (Zoom yapılandırıldı mı, benim Microsoft/Google takvimim bağlı mı).</summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var zoom = await _db.ProviderConfigs.AnyAsync(c => c.Provider == "Zoom" && c.IsEnabled, ct);
        var conns = me is null ? new() : await _db.CalendarConnections.Where(c => c.EmployeeId == me.Id && c.Status == "Active").Select(c => c.Provider).ToListAsync(ct);
        return Ok(new { zoom, teams = conns.Contains("Microsoft"), google = conns.Contains("Google"), calendar = conns.Count > 0 });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? sourceType, [FromQuery] Guid? sourceId, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var q = _db.Meetings.AsNoTracking().Where(m => m.Status == "Scheduled");
        if (sourceType is not null) q = q.Where(m => m.SourceType == sourceType);
        if (sourceId is not null) q = q.Where(m => m.SourceId == sourceId);
        if (sourceType is null && sourceId is null) q = q.Where(m => m.StartsAt >= DateTime.UtcNow.AddHours(-2));
        var list = await q.OrderBy(m => m.StartsAt).Take(100).ToListAsync(ct);
        if (!Me.IsHr) list = list.Where(m => me is not null && (m.OrganizerEmployeeId == me.Id || m.ParticipantEmployeeIds.Contains(me.Id))).ToList();
        return Ok(list.Select(View));
    }

    public record CreateMeeting(string SourceType, Guid? SourceId, string? Title, string? Description, DateTime? StartsAt, int DurationMinutes,
        string Provider, List<Guid>? ParticipantEmployeeIds, List<string>? ExternalEmails, bool IncludeCandidate, bool AddToCalendars);

    [HttpPost]
    public async Task<IActionResult> Create(CreateMeeting body, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return BadRequest(new { message = "Hesabınıza bağlı çalışan kaydı yok." });
        string title; DateTime start; var participants = new List<Guid>(); var externals = new List<string>(body.ExternalEmails ?? new());
        switch (body.SourceType)
        {
            case "one-on-one":
            {
                var o = (await Db.QueryAsync("""
                    SELECT o."ManagerUserId", o."EmployeeId", o."EmployeeName", o."ScheduledAt", o."Status" FROM engagement_one_on_ones o
                    WHERE o."TenantSlug" = $1 AND o."Id" = $2
                    """, r => (Mgr: r.GetString(0), Emp: r.GetGuid(1), Name: r.GetString(2), At: r.GetFieldValue<DateTime>(3), Status: r.GetString(4)), ct, Tenant, body.SourceId ?? Guid.Empty)).FirstOrDefault();
                if (o.Mgr is null) return NotFound(new { message = "1:1 bulunamadı." });
                if (o.Mgr != Me.UserId && !Me.IsHr) return Forbid();
                if (o.Status != "Planned") return BadRequest(new { message = "Yalnızca planlı 1:1'e toplantı eklenebilir." });
                title = body.Title ?? $"1:1 — {me.Name} & {o.Name}";
                start = DateTime.SpecifyKind(o.At, DateTimeKind.Utc);
                participants.Add(o.Emp);
                break;
            }
            case "interview":
            {
                var i = (await Db.QueryAsync("""
                    SELECT i."InterviewerEmployeeId", i."ScheduledAt", c."FirstName" || ' ' || c."LastName", c."Email", p."Title", i."Type"
                    FROM recruitment_interviews i
                    JOIN recruitment_applications a ON a."Id" = i."ApplicationId"
                    JOIN recruitment_candidates c ON c."Id" = a."CandidateId"
                    LEFT JOIN recruitment_job_postings p ON p."Id" = a."JobPostingId"
                    WHERE i."TenantSlug" = $1 AND i."Id" = $2
                    """, r => (Interviewer: r.GetGuid(0), At: r.GetFieldValue<DateTimeOffset>(1), Candidate: r.GetString(2), Email: r.Str(3), Posting: r.Str(4), Type: r.Str(5)), ct, Tenant, body.SourceId ?? Guid.Empty)).FirstOrDefault();
                if (i.Candidate is null) return NotFound(new { message = "Mülakat bulunamadı." });
                if (i.Interviewer != me.Id && !Me.IsHr) return Forbid();
                title = body.Title ?? $"Mülakat — {i.Candidate}{(i.Posting is null ? "" : $" ({i.Posting})")}";
                start = i.At.UtcDateTime;
                if (i.Interviewer != me.Id) participants.Add(i.Interviewer);
                if (body.IncludeCandidate && i.Email is not null) externals.Add(i.Email);
                break;
            }
            case "custom":
                if (!Me.IsManager) return Forbid();
                if (string.IsNullOrWhiteSpace(body.Title) || body.StartsAt is null) return BadRequest(new { message = "Başlık ve başlangıç zamanı zorunlu." });
                title = body.Title.Trim();
                start = DateTime.SpecifyKind(body.StartsAt.Value.ToUniversalTime(), DateTimeKind.Utc);
                break;
            default:
                return BadRequest(new { message = "Kaynak one-on-one, interview ya da custom olmalı." });
        }
        if (body.StartsAt is { } explicitStart && body.SourceType != "custom") start = DateTime.SpecifyKind(explicitStart.ToUniversalTime(), DateTimeKind.Utc);
        participants.AddRange(body.ParticipantEmployeeIds ?? new());
        try
        {
            var m = await _cal.CreateMeetingAsync(_db, Tenant, me.Id, new MeetingRequest(body.SourceType, body.SourceId, title, body.Description, start,
                body.DurationMinutes <= 0 ? 30 : body.DurationMinutes, body.Provider, participants, externals, body.AddToCalendars), ct);
            return Ok(View(m));
        }
        catch (CalendarFlowException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        var m = await _db.Meetings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound();
        if (!Me.IsHr && (me is null || m.OrganizerEmployeeId != me.Id)) return Forbid();
        await _cal.CancelMeetingAsync(_db, m, ct);
        return NoContent();
    }

    public record AvailabilityQuery(List<Guid> EmployeeIds, DateTime From, DateTime To, int DurationMinutes);

    /// <summary>Kişilerin dolu aralıkları (başlıksız) ve herkesin boş olduğu saat önerileri.</summary>
    [HttpPost("availability")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Availability(AvailabilityQuery q, CancellationToken ct)
    {
        if (q.EmployeeIds.Count is 0 or > 20) return BadRequest(new { message = "1–20 kişi seçin." });
        var from = q.From.ToUniversalTime(); var to = q.To.ToUniversalTime();
        if (to <= from || to - from > TimeSpan.FromDays(31)) return BadRequest(new { message = "Aralık en fazla 31 gün olmalı." });
        var people = await _cal.AvailabilityAsync(_db, Tenant, q.EmployeeIds, from, to, ct);
        return Ok(new
        {
            people = people.Select(p => new { p.EmployeeId, p.Name, p.CalendarConnected, busy = p.Busy.Select(b => new { b.Start, b.End, b.Source }) }),
            suggestions = CalendarService.Suggest(people, from, to, q.DurationMinutes <= 0 ? 30 : q.DurationMinutes),
        });
    }
}
