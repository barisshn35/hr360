using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure.Calendar;

public sealed record TokenSet(string AccessToken, string? RefreshToken, int ExpiresIn);
public sealed record Attendee(string Email, string? Name);

/// <summary>Takvime yazılacak etkinlik. Saatli etkinlikte Start/End UTC; tüm gün etkinlikte AllDayStart/AllDayEnd (dahil).</summary>
public sealed record CalendarEventSpec(
    string Title, string? Description, DateTime? Start, DateTime? End, DateOnly? AllDayStart, DateOnly? AllDayEnd,
    IReadOnlyList<Attendee> Attendees, bool NativeOnlineMeeting, string? Location, bool OutOfOffice);

public sealed record CreatedEvent(string Id, string? JoinUrl, string? WebLink);

public sealed class ProviderApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Google Takvim ve Microsoft 365 (Outlook) için ortak sözleşme.</summary>
public interface ICalendarProvider
{
    string Name { get; }
    string AuthorizeUrl(ProviderConfig cfg, string state, string codeChallenge, string redirectUri);
    Task<TokenSet> ExchangeCodeAsync(ProviderConfig cfg, string code, string verifier, string redirectUri, CancellationToken ct);
    Task<TokenSet> RefreshAsync(ProviderConfig cfg, string refreshToken, CancellationToken ct);
    Task<string?> AccountEmailAsync(string accessToken, CancellationToken ct);
    Task<CreatedEvent> CreateEventAsync(string accessToken, CalendarEventSpec e, CancellationToken ct);
    Task DeleteEventAsync(string accessToken, string eventId, CancellationToken ct);
    Task<List<(DateTime Start, DateTime End)>> BusyAsync(string accessToken, string? email, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

internal static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement?> SendAsync(IHttpClientFactory f, HttpMethod method, string url, string? bearer, HttpContent? content,
        CancellationToken ct, IDictionary<string, string>? headers = null, string what = "")
    {
        var client = f.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(method, url) { Content = content };
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (headers is not null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string? detail = null;
            try
            {
                var j = JsonDocument.Parse(body).RootElement;
                detail = j.TryGetProperty("error_description", out var d) ? d.GetString()
                    : j.TryGetProperty("error", out var e) ? (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString())
                    : j.TryGetProperty("message", out var mm) ? mm.GetString() : null;
            }
            catch (JsonException) { }
            throw new ProviderApiException($"{what} HTTP {(int)res.StatusCode}{(detail is null ? "" : ": " + detail.Split('\n')[0])}", res.StatusCode);
        }
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { return null; }
    }

    public static TokenSet ToTokens(JsonElement? j)
    {
        var e = j ?? throw new ProviderApiException("Jeton yanıtı boş");
        return new(e.GetProperty("access_token").GetString()!,
            e.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            e.TryGetProperty("expires_in", out var x) ? (x.ValueKind == JsonValueKind.Number ? x.GetInt32() : int.Parse(x.GetString()!, CultureInfo.InvariantCulture)) : 3600);
    }

    public static string Q(params (string K, string V)[] p) => string.Join("&", p.Select(x => $"{x.K}={Uri.EscapeDataString(x.V)}"));
}

public sealed class GoogleCalendar(IHttpClientFactory http) : ICalendarProvider
{
    private static readonly string AuthUrl = EnvVar.Or("GOOGLE_AUTH_URL", "https://accounts.google.com/o/oauth2/v2/auth");
    private static readonly string OAuthBase = EnvVar.Or("GOOGLE_OAUTH_BASE", "https://oauth2.googleapis.com").TrimEnd('/');
    private static readonly string Api = EnvVar.Or("GOOGLE_API_BASE", "https://www.googleapis.com").TrimEnd('/');
    public const string Scopes = "openid email https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.freebusy";
    public string Name => "Google";

    public string AuthorizeUrl(ProviderConfig cfg, string state, string codeChallenge, string redirectUri) =>
        AuthUrl + "?" + Http.Q(("client_id", cfg.ClientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("scope", Scopes),
            ("access_type", "offline"), ("prompt", "consent"), ("include_granted_scopes", "true"), ("state", state),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"));

    private Task<JsonElement?> Token(Dictionary<string, string> form, CancellationToken ct) =>
        Http.SendAsync(http, HttpMethod.Post, $"{OAuthBase}/token", null, new FormUrlEncodedContent(form), ct, what: "Google jeton");

    public async Task<TokenSet> ExchangeCodeAsync(ProviderConfig cfg, string code, string verifier, string redirectUri, CancellationToken ct) =>
        Http.ToTokens(await Token(new() { ["code"] = code, ["client_id"] = cfg.ClientId, ["client_secret"] = SecretBox.Unprotect(cfg.ClientSecretEnc) ?? "",
            ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier }, ct));

    public async Task<TokenSet> RefreshAsync(ProviderConfig cfg, string refreshToken, CancellationToken ct) =>
        Http.ToTokens(await Token(new() { ["client_id"] = cfg.ClientId, ["client_secret"] = SecretBox.Unprotect(cfg.ClientSecretEnc) ?? "",
            ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }, ct));

    public async Task<string?> AccountEmailAsync(string accessToken, CancellationToken ct)
    {
        var j = await Http.SendAsync(http, HttpMethod.Get, $"{Api}/oauth2/v3/userinfo", accessToken, null, ct, what: "Google kullanıcı bilgisi");
        return j is { } e && e.TryGetProperty("email", out var m) ? m.GetString() : null;
    }

    public async Task<CreatedEvent> CreateEventAsync(string accessToken, CalendarEventSpec e, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["summary"] = e.Title,
            ["description"] = e.Description,
            ["transparency"] = "opaque",
            ["reminders"] = new JsonObject { ["useDefault"] = true },
        };
        if (e.AllDayStart is { } ds)
        {
            body["start"] = new JsonObject { ["date"] = ds.ToString("yyyy-MM-dd") };
            body["end"] = new JsonObject { ["date"] = (e.AllDayEnd ?? ds).AddDays(1).ToString("yyyy-MM-dd") };
        }
        else
        {
            body["start"] = new JsonObject { ["dateTime"] = e.Start!.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["timeZone"] = "Europe/Istanbul" };
            body["end"] = new JsonObject { ["dateTime"] = e.End!.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["timeZone"] = "Europe/Istanbul" };
        }
        if (e.Location is not null) body["location"] = e.Location;
        if (e.Attendees.Count > 0)
            body["attendees"] = new JsonArray(e.Attendees.Select(a => (JsonNode)new JsonObject { ["email"] = a.Email, ["displayName"] = a.Name }).ToArray());
        if (e.NativeOnlineMeeting)
            body["conferenceData"] = new JsonObject
            {
                ["createRequest"] = new JsonObject { ["requestId"] = Guid.NewGuid().ToString("N"), ["conferenceSolutionKey"] = new JsonObject { ["type"] = "hangoutsMeet" } },
            };
        var r = (await Http.SendAsync(http, HttpMethod.Post, $"{Api}/calendar/v3/calendars/primary/events?conferenceDataVersion=1&sendUpdates=all", accessToken,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct, what: "Google Takvim etkinliği"))!.Value;
        string? join = r.TryGetProperty("hangoutLink", out var h) ? h.GetString() : null;
        return new(r.GetProperty("id").GetString()!, join, r.TryGetProperty("htmlLink", out var l) ? l.GetString() : null);
    }

    public async Task DeleteEventAsync(string accessToken, string eventId, CancellationToken ct)
    {
        try { await Http.SendAsync(http, HttpMethod.Delete, $"{Api}/calendar/v3/calendars/primary/events/{Uri.EscapeDataString(eventId)}?sendUpdates=all", accessToken, null, ct, what: "Google Takvim silme"); }
        catch (ProviderApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone) { }
    }

    public async Task<List<(DateTime Start, DateTime End)>> BusyAsync(string accessToken, string? email, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var body = new JsonObject { ["timeMin"] = fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["timeMax"] = toUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["items"] = new JsonArray(new JsonObject { ["id"] = "primary" }) };
        var r = (await Http.SendAsync(http, HttpMethod.Post, $"{Api}/calendar/v3/freeBusy", accessToken,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct, what: "Google boş/dolu"))!.Value;
        return r.GetProperty("calendars").GetProperty("primary").GetProperty("busy").EnumerateArray()
            .Select(b => (DateTime.Parse(b.GetProperty("start").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                          DateTime.Parse(b.GetProperty("end").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal))).ToList();
    }
}

public sealed class MicrosoftCalendar(IHttpClientFactory http) : ICalendarProvider
{
    private static readonly string Login = EnvVar.Or("MS_LOGIN_BASE", "https://login.microsoftonline.com").TrimEnd('/');
    private static readonly string Graph = EnvVar.Or("GRAPH_BASE", "https://graph.microsoft.com/v1.0").TrimEnd('/');
    public const string Scopes = "offline_access openid email User.Read Calendars.ReadWrite";
    private const string Tz = "Turkey Standard Time";
    public string Name => "Microsoft";

    private static string TenantOf(ProviderConfig cfg) => string.IsNullOrWhiteSpace(cfg.MsTenant) ? "organizations" : cfg.MsTenant!;

    public string AuthorizeUrl(ProviderConfig cfg, string state, string codeChallenge, string redirectUri) =>
        $"{Login}/{Uri.EscapeDataString(TenantOf(cfg))}/oauth2/v2.0/authorize?" + Http.Q(("client_id", cfg.ClientId), ("response_type", "code"),
            ("redirect_uri", redirectUri), ("response_mode", "query"), ("scope", Scopes), ("state", state), ("prompt", "select_account"),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"));

    private Task<JsonElement?> Token(ProviderConfig cfg, Dictionary<string, string> form, CancellationToken ct) =>
        Http.SendAsync(http, HttpMethod.Post, $"{Login}/{Uri.EscapeDataString(TenantOf(cfg))}/oauth2/v2.0/token", null, new FormUrlEncodedContent(form), ct, what: "Microsoft jeton");

    public async Task<TokenSet> ExchangeCodeAsync(ProviderConfig cfg, string code, string verifier, string redirectUri, CancellationToken ct) =>
        Http.ToTokens(await Token(cfg, new() { ["client_id"] = cfg.ClientId, ["client_secret"] = SecretBox.Unprotect(cfg.ClientSecretEnc) ?? "",
            ["code"] = code, ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier, ["scope"] = Scopes }, ct));

    public async Task<TokenSet> RefreshAsync(ProviderConfig cfg, string refreshToken, CancellationToken ct) =>
        Http.ToTokens(await Token(cfg, new() { ["client_id"] = cfg.ClientId, ["client_secret"] = SecretBox.Unprotect(cfg.ClientSecretEnc) ?? "",
            ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token", ["scope"] = Scopes }, ct));

    public async Task<string?> AccountEmailAsync(string accessToken, CancellationToken ct)
    {
        var j = await Http.SendAsync(http, HttpMethod.Get, $"{Graph}/me?$select=mail,userPrincipalName", accessToken, null, ct, what: "Microsoft profil");
        if (j is not { } e) return null;
        return (e.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
            ?? (e.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null);
    }

    private static string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Istanbul).ToString("yyyy-MM-ddTHH:mm:ss");

    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Istanbul", out var tz) ? tz : TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT");

    public async Task<CreatedEvent> CreateEventAsync(string accessToken, CalendarEventSpec e, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["subject"] = e.Title,
            ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = e.Description ?? "" },
            ["showAs"] = e.OutOfOffice ? "oof" : "busy",
            ["isReminderOn"] = !e.OutOfOffice,
        };
        if (e.AllDayStart is { } ds)
        {
            body["isAllDay"] = true;
            body["start"] = new JsonObject { ["dateTime"] = ds.ToString("yyyy-MM-dd") + "T00:00:00", ["timeZone"] = Tz };
            body["end"] = new JsonObject { ["dateTime"] = (e.AllDayEnd ?? ds).AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00", ["timeZone"] = Tz };
        }
        else
        {
            body["start"] = new JsonObject { ["dateTime"] = Local(e.Start!.Value), ["timeZone"] = Tz };
            body["end"] = new JsonObject { ["dateTime"] = Local(e.End!.Value), ["timeZone"] = Tz };
        }
        if (e.Location is not null) body["location"] = new JsonObject { ["displayName"] = e.Location };
        if (e.Attendees.Count > 0)
            body["attendees"] = new JsonArray(e.Attendees.Select(a => (JsonNode)new JsonObject
            {
                ["emailAddress"] = new JsonObject { ["address"] = a.Email, ["name"] = a.Name ?? a.Email }, ["type"] = "required",
            }).ToArray());
        if (e.NativeOnlineMeeting) { body["isOnlineMeeting"] = true; body["onlineMeetingProvider"] = "teamsForBusiness"; }
        var r = (await Http.SendAsync(http, HttpMethod.Post, $"{Graph}/me/events", accessToken,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct, what: "Outlook etkinliği"))!.Value;
        string? join = r.TryGetProperty("onlineMeeting", out var om) && om.ValueKind == JsonValueKind.Object && om.TryGetProperty("joinUrl", out var j) ? j.GetString() : null;
        return new(r.GetProperty("id").GetString()!, join, r.TryGetProperty("webLink", out var w) ? w.GetString() : null);
    }

    public async Task DeleteEventAsync(string accessToken, string eventId, CancellationToken ct)
    {
        try { await Http.SendAsync(http, HttpMethod.Delete, $"{Graph}/me/events/{Uri.EscapeDataString(eventId)}", accessToken, null, ct, what: "Outlook silme"); }
        catch (ProviderApiException ex) when (ex.Status is HttpStatusCode.NotFound) { }
    }

    public async Task<List<(DateTime Start, DateTime End)>> BusyAsync(string accessToken, string? email, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["schedules"] = new JsonArray(email),
            ["startTime"] = new JsonObject { ["dateTime"] = fromUtc.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
            ["endTime"] = new JsonObject { ["dateTime"] = toUtc.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
            ["availabilityViewInterval"] = 30,
        };
        var r = (await Http.SendAsync(http, HttpMethod.Post, $"{Graph}/me/calendar/getSchedule", accessToken,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct,
            new Dictionary<string, string> { ["Prefer"] = "outlook.timezone=\"UTC\"" }, "Outlook boş/dolu"))!.Value;
        var items = r.GetProperty("value")[0];
        if (!items.TryGetProperty("scheduleItems", out var si)) return new();
        return si.EnumerateArray().Where(i => i.GetProperty("status").GetString() is not ("free" or "workingElsewhere"))
            .Select(i => (DateTime.SpecifyKind(DateTime.Parse(i.GetProperty("start").GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture), DateTimeKind.Utc),
                          DateTime.SpecifyKind(DateTime.Parse(i.GetProperty("end").GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture), DateTimeKind.Utc))).ToList();
    }
}

/// <summary>Zoom Server-to-Server OAuth: hesap düzeyinde jeton, kullanıcı adına toplantı.</summary>
public sealed class ZoomApi(IHttpClientFactory http)
{
    private static readonly string OAuth = EnvVar.Or("ZOOM_OAUTH_BASE", "https://zoom.us").TrimEnd('/');
    private static readonly string Api = EnvVar.Or("ZOOM_API_BASE", "https://api.zoom.us/v2").TrimEnd('/');
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Exp)> Tokens = new();

    public async Task<string> TokenAsync(ProviderConfig cfg, CancellationToken ct)
    {
        // Anahtar gizli anahtarın özetini de içerir: bilgiler değişince eski jeton kullanılmaz.
        var key = $"{cfg.ZoomAccountId}:{cfg.ClientId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cfg.ClientSecretEnc ?? "")))[..16]}";
        if (Tokens.TryGetValue(key, out var c) && c.Exp > DateTime.UtcNow.AddMinutes(2)) return c.Token;
        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OAuth}/oauth/token?grant_type=account_credentials&account_id={Uri.EscapeDataString(cfg.ZoomAccountId ?? "")}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.ClientId}:{SecretBox.Unprotect(cfg.ClientSecretEnc)}")));
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string? reason = null;
            try { reason = JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString(); } catch { }
            throw new ProviderApiException($"Zoom jetonu alınamadı (HTTP {(int)res.StatusCode}){(reason is null ? "" : ": " + reason)}", res.StatusCode);
        }
        var t = Http.ToTokens(JsonDocument.Parse(body).RootElement);
        Tokens[key] = (t.AccessToken, DateTime.UtcNow.AddSeconds(t.ExpiresIn));
        return t.AccessToken;
    }

    public async Task<(string Id, string JoinUrl)> CreateMeetingAsync(ProviderConfig cfg, string hostUser, string topic, string? agenda, DateTime startUtc, int minutes, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        var body = new JsonObject
        {
            ["topic"] = topic.Length > 200 ? topic[..200] : topic, ["type"] = 2, ["start_time"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["duration"] = minutes, ["timezone"] = "Europe/Istanbul", ["agenda"] = agenda,
            ["settings"] = new JsonObject { ["join_before_host"] = false, ["waiting_room"] = true, ["mute_upon_entry"] = true },
        };
        var r = (await Http.SendAsync(http, HttpMethod.Post, $"{Api}/users/{Uri.EscapeDataString(hostUser)}/meetings", token,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct, what: "Zoom toplantısı"))!.Value;
        return (r.GetProperty("id").ToString(), r.GetProperty("join_url").GetString()!);
    }

    public async Task DeleteMeetingAsync(ProviderConfig cfg, string id, CancellationToken ct)
    {
        var token = await TokenAsync(cfg, ct);
        try { await Http.SendAsync(http, HttpMethod.Delete, $"{Api}/meetings/{Uri.EscapeDataString(id)}", token, null, ct, what: "Zoom iptal"); }
        catch (ProviderApiException ex) when (ex.Status is HttpStatusCode.NotFound) { }
    }
}

public static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
