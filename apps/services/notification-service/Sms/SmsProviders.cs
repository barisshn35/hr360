using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotificationService.Sms;

/// <summary>Sağlayıcıya gönderilecek tek ileti; Phone E.164 rakamları (artı işaretsiz, ör. 905321234567).</summary>
public sealed record SmsMessage(string Phone, string Text);

/// <summary>Gönderim sonucu: başarılıysa sağlayıcının iş kimliği, değilse insan okur hata (gizli bilgi içermez).</summary>
public sealed record SmsResult(bool Ok, string? Reference, string? Error, bool Permanent = false);

/// <summary>Bir kiracının çözülmüş sağlayıcı ayarı (gizli anahtar açık metin; günlüğe yazılmaz).</summary>
public sealed record SmsAccount(string Provider, string Username, string Secret, string Sender);

/// <summary>
/// Yurt içi SMS sağlayıcıları. Uç adresleri yalnızca ortam değişkeninden değişir (test: chatmock);
/// kiracı adres giremez, bu yüzden SSRF yüzeyi yoktur. İleti türü bilgilendirme (İYS dışı, ticari değil).
/// </summary>
public static class SmsProviders
{
    public const string Netgsm = "netgsm";
    public const string IletiMerkezi = "iletimerkezi";
    public static readonly string[] All = [Netgsm, IletiMerkezi];

    static string Base(string env, string fallback) =>
        (Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? v : fallback).TrimEnd('/');

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public static async Task<SmsResult> SendAsync(HttpClient http, SmsAccount a, SmsMessage m, CancellationToken ct)
    {
        try
        {
            return a.Provider switch
            {
                Netgsm => await NetgsmAsync(http, a, m, ct),
                IletiMerkezi => await IletiMerkeziAsync(http, a, m, ct),
                _ => new SmsResult(false, null, "Bilinmeyen SMS sağlayıcısı", Permanent: true),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SmsResult(false, null, $"Sağlayıcıya ulaşılamadı ({ex.GetType().Name})");
        }
    }

    /// <summary>Netgsm REST v2: POST /sms/rest/v2/send, Basic kimlik (abone kodu:parola), yanıt kodu "00" başarı.</summary>
    static async Task<SmsResult> NetgsmAsync(HttpClient http, SmsAccount a, SmsMessage m, CancellationToken ct)
    {
        var url = Base("NETGSM_API_BASE", "https://api.netgsm.com.tr") + "/sms/rest/v2/send";
        // Türkiye numaraları 10 hane (5XXXXXXXXX), yurt dışı 00 önekiyle.
        var no = m.Phone.StartsWith("90") && m.Phone.Length == 12 ? m.Phone[2..] : "00" + m.Phone;
        var body = new
        {
            msgheader = a.Sender,
            encoding = "TR",
            iysfilter = "0", // bilgilendirme iletisi (İYS kontrolü yok)
            messages = new[] { new { msg = m.Text, no } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{a.Username}:{a.Secret}")));
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode >= 500) return new SmsResult(false, null, $"Netgsm geçici hata (HTTP {(int)resp.StatusCode})");
        string? code = null, jobId = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            code = doc.RootElement.TryGetProperty("code", out var c) ? c.ToString() : null;
            jobId = doc.RootElement.TryGetProperty("jobid", out var j) ? j.ToString() : null;
        }
        catch (JsonException) { }
        if (resp.IsSuccessStatusCode && code == "00") return new SmsResult(true, jobId, null);
        return new SmsResult(false, null, NetgsmError(code, (int)resp.StatusCode), Permanent: code is "30" or "40" or "20" or "70");
    }

    static string NetgsmError(string? code, int http) => code switch
    {
        "20" => "Netgsm: ileti metni hatalı ya da çok uzun",
        "30" => "Netgsm: kullanıcı adı / parola hatalı ya da API erişimi kapalı (IP kısıtı olabilir)",
        "40" => "Netgsm: gönderici adı (başlık) tanımlı değil",
        "50" or "51" => "Netgsm: İYS kontrollü gönderim hatası",
        "70" => "Netgsm: hatalı istek parametresi",
        "80" => "Netgsm: gönderim sınırı aşıldı",
        "85" => "Netgsm: aynı numaraya kısa sürede çok fazla ileti",
        _ => $"Netgsm gönderimi reddetti (kod {code ?? "-"}, HTTP {http})",
    };

    /// <summary>
    /// İleti Merkezi v1: POST /v1/send-sms/json; kimlik: API anahtarı + HMAC-SHA256(anahtar, gizli anahtar).
    /// Yanıt status.code "200" başarı.
    /// </summary>
    static async Task<SmsResult> IletiMerkeziAsync(HttpClient http, SmsAccount a, SmsMessage m, CancellationToken ct)
    {
        var url = Base("ILETIMERKEZI_API_BASE", "https://api.iletimerkezi.com") + "/v1/send-sms/json";
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(a.Secret), Encoding.UTF8.GetBytes(a.Username))).ToLowerInvariant();
        var body = new
        {
            request = new
            {
                authentication = new { key = a.Username, hash },
                order = new
                {
                    sender = a.Sender,
                    iys = "0",
                    message = new { text = m.Text, receipents = new { number = new[] { "+" + m.Phone } } },
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode >= 500) return new SmsResult(false, null, $"İleti Merkezi geçici hata (HTTP {(int)resp.StatusCode})");
        string? code = null, msg = null, orderId = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement.GetProperty("response");
            if (r.TryGetProperty("status", out var st))
            {
                code = st.TryGetProperty("code", out var c) ? c.ToString() : null;
                msg = st.TryGetProperty("message", out var mm) ? mm.GetString() : null;
            }
            if (r.TryGetProperty("order", out var o) && o.TryGetProperty("id", out var id)) orderId = id.ToString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        if (code == "200") return new SmsResult(true, orderId, null);
        var permanent = code is "401" or "402" or "450" or "451" or "452";
        return new SmsResult(false, null, $"İleti Merkezi gönderimi reddetti (kod {code ?? (((int)resp.StatusCode).ToString())}{(msg is null ? "" : ": " + Trim(msg, 120))})", permanent);
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>Telefon ve metin biçimleme; saf mantık, birim testli.</summary>
public static class SmsFormat
{
    /// <summary>SMS metni üst sınırı (yaklaşık 4 ileti parçası); aşan metin kısaltılır.</summary>
    public const int MaxLength = 480;

    /// <summary>
    /// E.164 rakamları (artısız) ya da geçersizse null. Türkiye biçimleri: 05XX…, 5XX…, 905XX…, +90…, 0090….
    /// Diğer ülkeler yalnızca + ya da 00 önekiyle (8–15 hane).
    /// </summary>
    public static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var intl = s.StartsWith('+') || s.StartsWith("00");
        var d = new string(s.Where(char.IsDigit).ToArray());
        if (d.StartsWith("00")) d = d[2..];
        if (!intl)
        {
            if (d.Length == 11 && d.StartsWith('0')) d = "90" + d[1..];
            else if (d.Length == 10) d = "90" + d;
            else if (!(d.Length == 12 && d.StartsWith("90"))) return null;
        }
        if (d.StartsWith("90") && d.Length != 12) return null;
        return d.Length is >= 8 and <= 15 && d[0] != '0' ? d : null;
    }

    /// <summary>Konu + gövde + (varsa) bağlantı; satır sonları sadeleşir, uzun metin kısaltılır.</summary>
    public static string Compose(string? subject, string body, string? actionUrl)
    {
        var text = string.IsNullOrWhiteSpace(subject) ? body.Trim() : $"{subject.Trim()}: {body.Trim()}";
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\n\s*", " ").Trim();
        var link = actionUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? " " + u : "";
        var room = MaxLength - link.Length;
        if (text.Length > room) text = text[..Math.Max(0, room - 1)].TrimEnd() + "…";
        return text + link;
    }

    /// <summary>Ekran ve kayıt için maskeli numara: son 4 hane görünür.</summary>
    public static string Mask(string phone) => phone.Length <= 4 ? "****" : new string('*', phone.Length - 4) + phone[^4..];
}
