using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GovernanceService.Infrastructure.Chat;

public sealed class ChatApiException(string message) : Exception(message);

/// <summary>Ortam degiskeni; tanimsiz ya da bos ise varsayilan (compose bos dize gecebilir).</summary>
public static class EnvVar
{
    public static string Or(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;
}

/// <summary>
/// Slack Web API istemcisi. Tum cagrilar form-urlencoded POST ile yapilir (Slack'in
/// okuma metotlari JSON govde kabul etmez). SLACK_API_BASE testlerde sahte sunucuya
/// yonlendirmek icindir.
/// </summary>
public sealed class SlackApi
{
    private readonly IHttpClientFactory _http;
    private static readonly string Base = (EnvVar.Or("SLACK_API_BASE", "https://slack.com/api")).TrimEnd('/');

    public SlackApi(IHttpClientFactory http) => _http = http;

    public async Task<JsonElement> CallAsync(string token, string method, Dictionary<string, string?> args, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{method}")
        {
            Content = new FormUrlEncodedContent(args.Where(a => a.Value is not null).Select(a => new KeyValuePair<string, string>(a.Key, a.Value!))),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new ChatApiException($"Slack {method}: HTTP {(int)res.StatusCode}"); }
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new ChatApiException($"Slack {method}: {(root.TryGetProperty("error", out var e) ? e.GetString() : "bilinmeyen hata")}");
        return root;
    }

    public async Task<(string TeamId, string Team, string BotUserId)> AuthTestAsync(string token, CancellationToken ct)
    {
        var r = await CallAsync(token, "auth.test", new(), ct);
        return (r.GetProperty("team_id").GetString() ?? "", r.TryGetProperty("team", out var t) ? t.GetString() ?? "" : "",
            r.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "");
    }

    public async Task<string?> LookupByEmailAsync(string token, string email, CancellationToken ct)
    {
        try { return (await CallAsync(token, "users.lookupByEmail", new() { ["email"] = email }, ct)).GetProperty("user").GetProperty("id").GetString(); }
        catch (ChatApiException ex) when (ex.Message.Contains("users_not_found")) { return null; }
    }

    public async Task<(string? Email, string? Name)> UserInfoAsync(string token, string userId, CancellationToken ct)
    {
        var u = (await CallAsync(token, "users.info", new() { ["user"] = userId }, ct)).GetProperty("user");
        var profile = u.TryGetProperty("profile", out var p) ? p : default;
        string? Get(JsonElement el, string name) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v.GetString() : null;
        return (Get(profile, "email"), Get(profile, "real_name") ?? Get(u, "real_name") ?? Get(u, "name"));
    }

    public async Task<string> OpenDmAsync(string token, string userId, CancellationToken ct) =>
        (await CallAsync(token, "conversations.open", new() { ["users"] = userId }, ct)).GetProperty("channel").GetProperty("id").GetString()!;

    public async Task<string> PostAsync(string token, string channel, string text, JsonArray? blocks, CancellationToken ct) =>
        (await CallAsync(token, "chat.postMessage", new() { ["channel"] = channel, ["text"] = text, ["blocks"] = blocks?.ToJsonString() }, ct))
            .GetProperty("ts").GetString()!;

    public Task UpdateAsync(string token, string channel, string ts, string text, JsonArray? blocks, CancellationToken ct) =>
        CallAsync(token, "chat.update", new() { ["channel"] = channel, ["ts"] = ts, ["text"] = text, ["blocks"] = blocks?.ToJsonString() ?? "[]" }, ct);

    /// <summary>Etkileşimden (düğme) form penceresi açar; trigger_id 3 sn geçerlidir.</summary>
    public Task OpenViewAsync(string token, string triggerId, JsonObject view, CancellationToken ct) =>
        CallAsync(token, "views.open", new() { ["trigger_id"] = triggerId, ["view"] = view.ToJsonString() }, ct);

    /// <summary>Kişinin App Home sekmesini yayınlar (yalnızca o kişi görür).</summary>
    public Task PublishHomeAsync(string token, string userId, JsonObject view, CancellationToken ct) =>
        CallAsync(token, "views.publish", new() { ["user_id"] = userId, ["view"] = view.ToJsonString() }, ct);

    /// <summary>Slack istek imzasi (v0, HMAC-SHA256, 5 dk zaman penceresi).</summary>
    public static bool VerifySignature(string secret, string timestamp, string signature, string rawBody)
    {
        if (!long.TryParse(timestamp, out var t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300) return false;
        var expected = "v0=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{rawBody}"))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }
}

/// <summary>
/// Microsoft Teams (Azure Bot Service) Bot Connector istemcisi. Bot, Azure'da
/// tek kiracili (single-tenant) olarak kaydedilir; erisim jetonu o Entra ID
/// kiracisindan alinir. TEAMS_LOGIN_BASE testlerde sahte sunucu icindir.
/// </summary>
public sealed class TeamsApi
{
    private readonly IHttpClientFactory _http;
    private static readonly string LoginBase = (EnvVar.Or("TEAMS_LOGIN_BASE", "https://login.microsoftonline.com")).TrimEnd('/');
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Exp)> Tokens = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TeamsApi(IHttpClientFactory http) => _http = http;

    public async Task<string> TokenAsync(string appId, string password, string azureTenantId, CancellationToken ct)
    {
        var key = $"{azureTenantId}:{appId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)))[..16]}";
        if (Tokens.TryGetValue(key, out var cached) && cached.Exp > DateTime.UtcNow.AddMinutes(2)) return cached.Token;
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var res = await client.PostAsync($"{LoginBase}/{Uri.EscapeDataString(azureTenantId)}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = appId, ["client_secret"] = password,
            ["scope"] = "https://api.botframework.com/.default",
        }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string? err = null;
            try { err = JsonDocument.Parse(body).RootElement.GetProperty("error_description").GetString(); } catch { }
            throw new ChatApiException($"Teams jetonu alınamadı (HTTP {(int)res.StatusCode}){(err is null ? "" : ": " + err.Split('\n')[0])}");
        }
        var doc = JsonDocument.Parse(body).RootElement;
        var token = doc.GetProperty("access_token").GetString()!;
        var exp = DateTime.UtcNow.AddSeconds(doc.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600);
        Tokens[key] = (token, exp);
        return token;
    }

    private async Task<JsonElement?> SendAsync(HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ChatApiException($"Teams {method} {new Uri(url).AbsolutePath}: HTTP {(int)res.StatusCode}");
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { return null; }
    }

    private static string Conv(string serviceUrl, string conversationId) =>
        $"{serviceUrl.TrimEnd('/')}/v3/conversations/{Uri.EscapeDataString(conversationId)}";

    public async Task<string?> PostActivityAsync(string token, string serviceUrl, string conversationId, object activity, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Post, $"{Conv(serviceUrl, conversationId)}/activities", token, activity, ct);
        return r is { } el && el.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public Task UpdateActivityAsync(string token, string serviceUrl, string conversationId, string activityId, object activity, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Conv(serviceUrl, conversationId)}/activities/{Uri.EscapeDataString(activityId)}", token, activity, ct);

    public async Task<(string? Email, string? Name, string? AadObjectId)> MemberAsync(string token, string serviceUrl, string conversationId, string userId, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"{Conv(serviceUrl, conversationId)}/members/{Uri.EscapeDataString(userId)}", token, null, ct);
        if (r is not { } m) return (null, null, null);
        string? G(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return (G("email") ?? G("userPrincipalName"), G("name"), G("aadObjectId"));
    }
}

/// <summary>
/// Teams'ten (Bot Framework) gelen isteklerin JWT dogrulamasi: imza (Bot Framework
/// OpenID anahtarlari), yayinci api.botframework.com, hedef kitle = botun App ID'si
/// ve "serviceurl" talebinin etkinlikteki adresle ayni olmasi. Son kosul onemli:
/// cevaplar o adrese bot jetonuyla gonderilir.
/// </summary>
public sealed class BotFrameworkAuth
{
    private static readonly string Metadata = EnvVar.Or("TEAMS_OPENID_METADATA", "https://login.botframework.com/v1/.well-known/openidconfiguration");
    private const string Issuer = "https://api.botframework.com";
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _config = new(
        Metadata, new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = Metadata.StartsWith("https://", StringComparison.OrdinalIgnoreCase) })
    {
        // Anahtar listesi en fazla 30 sn'de bir zorla yenilenebilir (geçersiz jetonlarla yenileme seli olmasın).
        RefreshInterval = TimeSpan.FromSeconds(30),
    };

    public async Task<(bool Ok, string? Error)> ValidateAsync(string? authorization, string appId, string? activityServiceUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (false, "jeton yok");
        var token = authorization[7..].Trim();
        OpenIdConnectConfiguration cfg;
        try { cfg = await _config.GetConfigurationAsync(ct); }
        catch (Exception ex) { return (false, "Bot Framework anahtarları alınamadı: " + ex.Message); }
        var result = await ValidateWithAsync(token, appId, cfg);
        // Microsoft imza anahtarlarını düzenli olarak yeniler: tanınmayan anahtar ya da tutmayan imzada
        // önbellekteki liste bir kez yenilenip yeniden denenir (önceden yeni anahtar gelene kadar her istek
        // reddediliyordu; önbellek varsayılanı 12 saat).
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException)
        {
            _config.RequestRefresh();
            try { cfg = await _config.GetConfigurationAsync(ct); }
            catch (Exception ex) { return (false, "Bot Framework anahtarları alınamadı: " + ex.Message); }
            result = await ValidateWithAsync(token, appId, cfg);
        }
        if (!result.IsValid) return (false, result.Exception?.Message ?? "geçersiz jeton");
        var claimUrl = result.ClaimsIdentity.FindFirst("serviceurl")?.Value;
        if (activityServiceUrl is not null && (claimUrl is null
            || !string.Equals(claimUrl.TrimEnd('/'), activityServiceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            return (false, "serviceUrl jetondakiyle uyuşmuyor");
        return (true, null);
    }

    private static Task<TokenValidationResult> ValidateWithAsync(string token, string appId, OpenIdConnectConfiguration cfg) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = appId,
            IssuerSigningKeys = cfg.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            RequireSignedTokens = true,
        });
}
