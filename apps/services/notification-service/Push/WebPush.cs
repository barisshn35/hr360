using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Tenancy;

namespace NotificationService.Push;

/// <summary>
/// Web Push (RFC 8030) — içerik şifreleme RFC 8291 (aes128gcm), sunucu kimliği VAPID (RFC 8292).
/// Harici kütüphane yok; .NET'in ECDH, HKDF, AES-GCM ve ECDSA sınıfları kullanılır.
/// </summary>
public static class WebPushCrypto
{
    public static string B64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    static ECParameters FromUncompressed(byte[] point) => point.Length == 65 && point[0] == 4
        ? new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } }
        : throw new CryptographicException("P-256 açık anahtarı 65 bayt (sıkıştırılmamış) olmalı");

    static byte[] Uncompressed(ECParameters p) => [4, .. p.Q.X!, .. p.Q.Y!];

    /// <summary>Yeni VAPID anahtar çifti: (açık anahtar b64url 65 bayt, özel anahtar b64url 32 bayt).</summary>
    public static (string Public, string Private) NewVapidKeys()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        return (B64(Uncompressed(p)), B64(p.D!));
    }

    /// <summary>VAPID JWT (ES256): aud = uç noktanın kökeni, 12 saat geçerli.</summary>
    public static string VapidJwt(string endpoint, string publicKey, string privateKey, string subject, DateTimeOffset now)
    {
        var uri = new Uri(endpoint);
        var header = B64(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { aud = $"{uri.Scheme}://{uri.Authority}", exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject }));
        var pub = FromUncompressed(UnB64(publicKey));
        pub.D = UnB64(privateKey);
        using var ec = ECDsa.Create(pub);
        var sig = ec.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256); // IEEE P1363 (r||s)
        return $"{header}.{claims}.{B64(sig)}";
    }

    /// <summary>RFC 8291: istemcinin p256dh ve auth değerleriyle aes128gcm gövdesi üretir.</summary>
    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, byte[]? salt = null, ECDiffieHellman? asKey = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        using var ephemeral = asKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = Uncompressed(ephemeral.ExportParameters(false));
        using var ua = ECDiffieHellman.Create(FromUncompressed(uaPublic));
        var ecdhSecret = ephemeral.DeriveRawSecretAgreement(ua.PublicKey);

        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(uaPublic).Concat(asPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var record = plaintext.Append((byte)2).ToArray(); // son kayıt sınırlayıcısı
        var cipher = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, record, cipher, tag);

        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 4096);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return [.. header, .. cipher, .. tag];
    }
}

/// <summary>
/// Uç nokta güvenliği (SSRF): yalnızca bilinen tarayıcı push servislerine (HTTPS) gönderilir.
/// PUSH_ALLOWED_HOSTS ile ek alan adı (ör. kurum içi test sunucusu) tanımlanabilir.
/// </summary>
public static class PushEndpointPolicy
{
    static readonly string[] Known =
    {
        "fcm.googleapis.com", "android.googleapis.com", "updates.push.services.mozilla.com",
        "push.services.mozilla.com", "notify.windows.com", "push.apple.com",
    };

    public static bool IsAllowed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u)) return false;
        var extra = (Environment.GetEnvironmentVariable("PUSH_ALLOWED_HOSTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extra.Any(h => string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase))) return u.Scheme is "https" or "http";
        if (u.Scheme != "https" || !u.IsDefaultPort) return false;
        return Known.Any(k => u.Host.Equals(k, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + k, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>VAPID anahtarları: ilk kullanımda üretilir, özel anahtar TENANT_SECRET_KEY ile şifreli saklanır.</summary>
public class VapidKeys
{
    readonly IServiceProvider _sp;
    (string Public, string Private)? _cache;
    readonly SemaphoreSlim _lock = new(1, 1);
    public VapidKeys(IServiceProvider sp) => _sp = sp;

    public async Task<(string Public, string Private)> GetAsync(CancellationToken ct)
    {
        if (_cache is { } c) return c;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } c2) return c2;
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
            var prot = scope.ServiceProvider.GetRequiredService<Security.SmtpCredentialProtector>();
            var row = await db.VapidKeys.FirstOrDefaultAsync(ct);
            if (row is null)
            {
                var (pub, priv) = WebPushCrypto.NewVapidKeys();
                row = new VapidKeyRow { Id = 1, PublicKey = pub, PrivateKeyEnc = prot.Encrypt(priv) };
                db.VapidKeys.Add(row);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); row = await db.VapidKeys.FirstAsync(ct); } // eşzamanlı başlangıç
            }
            _cache = (row.PublicKey, prot.Decrypt(row.PrivateKeyEnc));
            return _cache.Value;
        }
        finally { _lock.Release(); }
    }
}

/// <summary>
/// Uygulama içi bildirimleri, kişinin anlık bildirim açtığı cihazlara iletir.
/// KVKK: push içeriğinde kişisel veri yoktur ("Yeni bir bildiriminiz var"); ayrıntı uygulamada,
/// oturum açıkken görülür. Geçersizleşen abonelik (404/410) silinir.
/// </summary>
public class PushSenderWorker : BackgroundService
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("PUSH_POLL_SECONDS"), out var s) ? Math.Max(2, s) : 15);
    readonly IServiceProvider _sp;
    readonly ILogger<PushSenderWorker> _log;
    readonly VapidKeys _keys;
    readonly HttpClient _http;

    public PushSenderWorker(IServiceProvider sp, ILogger<PushSenderWorker> log, VapidKeys keys, IHttpClientFactory http)
    {
        _sp = sp; _log = log; _keys = keys; _http = http.CreateClient("webpush");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RoundAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Anlık bildirim turu başarısız"); }
            await Task.Delay(Poll, ct);
        }
    }

    public async Task<int> RoundAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().IsPlatformAdmin = true;
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddMinutes(-30);
        var deferredFloor = now.AddDays(-2);
        // Yeni bildirimler (30 dk) ve sessiz saati biten ertelenmiş bildirimler.
        var candidates = await db.Notifications
            .Where(n => n.Channel == NotificationChannel.InApp && n.PushedAt == null
                        && (n.DeferredUntil == null ? n.CreatedAt > since : n.DeferredUntil <= now && n.DeferredUntil > deferredFloor))
            .OrderBy(n => n.CreatedAt).Take(200).ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        // G11 bildirim tercihleri: kategori için push / uygulama içi kapalıysa iletilmez;
        // sessiz saatteyse DeferredUntil'e ertelenir (uygulama içi kayıt etkilenmez).
        var prefs = await Preferences.PreferenceStore.LoadAsync(db, candidates.Select(n => (n.TenantSlug, n.RecipientEmployeeId)), ct);
        var fresh = new List<Notification>();
        foreach (var n in candidates)
        {
            prefs.TryGetValue((n.TenantSlug, n.RecipientEmployeeId), out var p);
            var (decision, until) = Preferences.DeliveryRules.DecidePush(n.TemplateCode, p, now);
            if (decision == Preferences.Delivery.Defer) { n.DeferredUntil = until; continue; }
            n.PushedAt = now;
            if (decision == Preferences.Delivery.Send) fresh.Add(n);
        }
        await db.SaveChangesAsync(ct);
        if (fresh.Count == 0) return 0;

        var sent = 0;
        foreach (var g in fresh.GroupBy(n => (n.TenantSlug, n.RecipientEmployeeId)))
        {
            var subs = await db.PushSubscriptions.Where(p => p.TenantSlug == g.Key.TenantSlug && p.EmployeeId == g.Key.RecipientEmployeeId).ToListAsync(ct);
            if (subs.Count == 0) continue;
            var en = g.Last().Language == "en";
            var count = g.Count();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = "HR360",
                body = en ? (count > 1 ? $"You have {count} new notifications" : "You have a new notification")
                          : (count > 1 ? $"{count} yeni bildiriminiz var" : "Yeni bir bildiriminiz var"),
                url = "/panel/bildirimler",
                tag = "hr360-notifications",
            });
            foreach (var sub in subs)
            {
                var status = await SendAsync(sub, payload, ct);
                if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) db.PushSubscriptions.Remove(sub);
                else if ((int)status is >= 200 and < 300) { sub.LastSuccessAt = DateTimeOffset.UtcNow; sub.FailureCount = 0; sent++; }
                else if (++sub.FailureCount >= 10) db.PushSubscriptions.Remove(sub);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    async Task<HttpStatusCode> SendAsync(PushSubscription sub, byte[] payload, CancellationToken ct)
    {
        if (!PushEndpointPolicy.IsAllowed(sub.Endpoint)) return HttpStatusCode.Gone;
        try
        {
            var (pub, priv) = await _keys.GetAsync(ct);
            var subject = Environment.GetEnvironmentVariable("PUSH_SUBJECT") is { Length: > 0 } s ? s
                : (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") is { Length: > 0 } o && o.StartsWith("https://") ? o : "mailto:noreply@hr360.local");
            var body = WebPushCrypto.Encrypt(WebPushCrypto.UnB64(sub.P256dh), WebPushCrypto.UnB64(sub.Auth), payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new("application/octet-stream");
            req.Content.Headers.ContentEncoding.Add("aes128gcm");
            req.Headers.Add("TTL", "86400");
            req.Headers.Add("Urgency", "normal");
            req.Headers.TryAddWithoutValidation("Authorization", $"vapid t={WebPushCrypto.VapidJwt(sub.Endpoint, pub, priv, subject, DateTimeOffset.UtcNow)}, k={pub}");
            using var resp = await _http.SendAsync(req, ct);
            return resp.StatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation("Anlık bildirim gönderilemedi: {Message}", ex.Message);
            return HttpStatusCode.ServiceUnavailable;
        }
    }
}
