using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GovernanceService.Infrastructure.Chat;

/* ======================================================================
 * B7: kendi sunucunuzda çalışan Mattermost ve Rocket.Chat. İkisi de gelen
 * istekleri paylaşılan bir jetonla doğrular (Mattermost slash komutu / giden
 * webhook "token" alanı; Rocket.Chat giden entegrasyon "token" alanı) ve
 * bot hesabıyla REST API üzerinden mesaj gönderir.
 * ==================================================================== */

public static class IncomingToken
{
    /// <summary>Sabit zamanlı karşılaştırma (boş beklenen değer her zaman reddedilir).</summary>
    public static bool Matches(string? expected, string? given) =>
        !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(given)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    /// <summary>Mattermost etkileşimli düğme bağlamı imzası (düğme isteği jeton taşımaz).</summary>
    public static string Sign(string secret, string action, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{action}|{value}"))).ToLowerInvariant()[..32];
}

/// <summary>Mattermost REST API v4 istemcisi (bot erişim jetonuyla).</summary>
public sealed class MattermostApi(IHttpClientFactory http)
{
    private async Task<JsonElement?> SendAsync(HttpMethod method, string baseUrl, string path, string token, object? body, CancellationToken ct)
    {
        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}/api/v4/{path.TrimStart('/')}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string? msg = null;
            try { msg = JsonDocument.Parse(text).RootElement.GetProperty("message").GetString(); } catch { }
            throw new ChatApiException($"Mattermost {method} {path.Split('?')[0]}: HTTP {(int)res.StatusCode}{(msg is null ? "" : " " + msg)}");
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { return null; }
    }

    private static string? S(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Jetonu doğrular; botun kullanıcı kimliği ve adı.</summary>
    public async Task<(string Id, string Username)> MeAsync(string baseUrl, string token, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, baseUrl, "users/me", token, null, ct);
        return (S(r, "id") ?? "", S(r, "username") ?? "");
    }

    public async Task<(string? Email, string? Name)> UserAsync(string baseUrl, string token, string userId, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, baseUrl, $"users/{Uri.EscapeDataString(userId)}", token, null, ct);
        var name = string.Join(' ', new[] { S(r, "first_name"), S(r, "last_name") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return (S(r, "email"), name.Length > 0 ? name : S(r, "username"));
    }

    public async Task<string?> UserIdByEmailAsync(string baseUrl, string token, string email, CancellationToken ct)
    {
        try { return S(await SendAsync(HttpMethod.Get, baseUrl, $"users/email/{Uri.EscapeDataString(email)}", token, null, ct), "id"); }
        catch (ChatApiException ex) when (ex.Message.Contains("HTTP 404")) { return null; }
    }

    public async Task<string> DirectChannelAsync(string baseUrl, string token, string botUserId, string userId, CancellationToken ct) =>
        S(await SendAsync(HttpMethod.Post, baseUrl, "channels/direct", token, new[] { botUserId, userId }, ct), "id")
            ?? throw new ChatApiException("Mattermost: DM kanalı açılamadı");

    public async Task<string?> PostAsync(string baseUrl, string token, string channelId, string message, JsonArray? attachments, CancellationToken ct)
    {
        var body = new JsonObject { ["channel_id"] = channelId, ["message"] = message };
        if (attachments is { Count: > 0 }) body["props"] = new JsonObject { ["attachments"] = attachments };
        return S(await SendAsync(HttpMethod.Post, baseUrl, "posts", token, body, ct), "id");
    }

    /// <summary>Mesajı yerinde günceller (düğmeler kaldırılır).</summary>
    public Task UpdateAsync(string baseUrl, string token, string postId, string message, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, baseUrl, $"posts/{Uri.EscapeDataString(postId)}/patch", token,
            new JsonObject { ["message"] = message, ["props"] = new JsonObject { ["attachments"] = new JsonArray() } }, ct);
}

/// <summary>Rocket.Chat REST API v1 istemcisi (kişisel erişim jetonu: X-Auth-Token + X-User-Id).</summary>
public sealed class RocketChatApi(IHttpClientFactory http)
{
    private async Task<JsonElement> SendAsync(HttpMethod method, string baseUrl, string path, string userId, string token, object? body, CancellationToken ct)
    {
        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}/api/v1/{path.TrimStart('/')}");
        req.Headers.Add("X-Auth-Token", token);
        req.Headers.Add("X-User-Id", userId);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new ChatApiException($"Rocket.Chat {path.Split('?')[0]}: HTTP {(int)res.StatusCode}"); }
        if (!res.IsSuccessStatusCode || (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False))
            throw new ChatApiException($"Rocket.Chat {path.Split('?')[0]}: HTTP {(int)res.StatusCode}{(root.TryGetProperty("error", out var e) ? " " + e : "")}");
        return root;
    }

    public async Task<(string Id, string Username)> MeAsync(string baseUrl, string userId, string token, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, baseUrl, "me", userId, token, null, ct);
        return (r.TryGetProperty("_id", out var i) ? i.GetString() ?? "" : "", r.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "");
    }

    public async Task<(string? Email, string? Name, string? Username)> UserAsync(string baseUrl, string botId, string token, string userId, CancellationToken ct)
    {
        var u = (await SendAsync(HttpMethod.Get, baseUrl, $"users.info?userId={Uri.EscapeDataString(userId)}", botId, token, null, ct)).GetProperty("user");
        string? email = u.TryGetProperty("emails", out var es) && es.ValueKind == JsonValueKind.Array && es.GetArrayLength() > 0
            && es[0].TryGetProperty("address", out var a) ? a.GetString() : null;
        return (email, u.TryGetProperty("name", out var n) ? n.GetString() : null, u.TryGetProperty("username", out var un) ? un.GetString() : null);
    }

    public async Task<(string? Id, string? Username)> UserByEmailAsync(string baseUrl, string botId, string token, string email, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string> { ["emails.address"] = email }));
        var r = await SendAsync(HttpMethod.Get, baseUrl, $"users.list?query={q}&count=1", botId, token, null, ct);
        if (!r.TryGetProperty("users", out var us) || us.GetArrayLength() == 0) return (null, null);
        return (us[0].GetProperty("_id").GetString(), us[0].TryGetProperty("username", out var n) ? n.GetString() : null);
    }

    public async Task<string> DirectRoomAsync(string baseUrl, string botId, string token, string username, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Post, baseUrl, "im.create", botId, token, new { username }, ct);
        return r.GetProperty("room").GetProperty("_id").GetString() ?? throw new ChatApiException("Rocket.Chat: DM odası açılamadı");
    }

    public async Task<string?> PostAsync(string baseUrl, string botId, string token, string roomId, string text, JsonArray? attachments, CancellationToken ct)
    {
        var body = new JsonObject { ["roomId"] = roomId, ["text"] = text };
        if (attachments is { Count: > 0 }) body["attachments"] = attachments;
        var r = await SendAsync(HttpMethod.Post, baseUrl, "chat.postMessage", botId, token, body, ct);
        return r.TryGetProperty("message", out var m) && m.TryGetProperty("_id", out var id) ? id.GetString() : null;
    }

    public Task UpdateAsync(string baseUrl, string botId, string token, string roomId, string msgId, string text, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, baseUrl, "chat.update", botId, token, new { roomId, msgId, text, attachments = Array.Empty<object>() }, ct);
}
