using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Preferences;
using NotificationService.Sms;
using Xunit;

namespace Notification.Tests;

public class SmsTests
{
    static readonly TimeZoneInfo Tz = QuietHoursCalc.Istanbul;
    static DateTimeOffset At(int h) => QuietHoursCalc.ToUtc(new DateTime(2026, 10, 5, h, 0, 0), Tz);

    static EffectivePrefs Prefs(bool sms = true, bool quiet = false, params (string Cat, ChannelPrefs Ch)[] cats) =>
        new(cats.ToDictionary(c => c.Cat, c => c.Ch), new QuietHours(quiet, new(22, 0), new(8, 0), 127), false, 18, sms);

    [Theory]
    [InlineData("0532 123 45 67", "905321234567")]
    [InlineData("5321234567", "905321234567")]
    [InlineData("905321234567", "905321234567")]
    [InlineData("+90 (532) 123-45-67", "905321234567")]
    [InlineData("0090 532 123 45 67", "905321234567")]
    [InlineData("+49 151 23456789", "4915123456789")]
    [InlineData("12345", null)]
    [InlineData("0532 123 45", null)]
    [InlineData("+90 532 123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Telefon_E164_bicimine_getirilir(string? raw, string? expected) =>
        Assert.Equal(expected, SmsFormat.NormalizePhone(raw));

    [Fact]
    public void Metin_konu_govde_ve_baglantiyi_birlestirir_uzunsa_kisaltir()
    {
        Assert.Equal("İzin: Talebiniz onaylandı. Ayrıntı", SmsFormat.Compose("İzin", "Talebiniz onaylandı.\n\nAyrıntı", null));
        Assert.Equal("Gövde https://x.test/a", SmsFormat.Compose(null, "Gövde", "https://x.test/a"));
        Assert.Equal("Gövde", SmsFormat.Compose(null, "Gövde", "javascript:alert(1)"));
        var longText = SmsFormat.Compose(null, new string('a', 1000), "https://x.test/a");
        Assert.Equal(SmsFormat.MaxLength, longText.Length);
        Assert.EndsWith("… https://x.test/a", longText);
        Assert.Equal("********4567", SmsFormat.Mask("905321234567"));
    }

    [Fact]
    public void Sms_karari_tercih_kritik_ve_sessiz_saat()
    {
        var day = At(12);
        var night = At(23);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideSms("leave.approved", null, day).Decision);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideSms("leave.approved", Prefs(), day).Decision);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecideSms("leave.approved", Prefs(sms: false), day).Decision);
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecideSms(null, Prefs(sms: false), day).Decision);
        // Kategori tümüyle kapalı (uygulama içi ve e-posta) ise SMS de gitmez.
        Assert.Equal(Delivery.Suppress, DeliveryRules.DecideSms("leave.approved", Prefs(true, false, ("leave", new(false, false, true, true))), day).Decision);
        Assert.Equal(Delivery.Send, DeliveryRules.DecideSms("leave.approved", Prefs(true, false, ("leave", new(false, true, false, false))), day).Decision);
        var (d, until) = DeliveryRules.DecideSms("leave.approved", Prefs(quiet: true), night);
        Assert.Equal(Delivery.Defer, d);
        Assert.Equal(QuietHoursCalc.ToUtc(new DateTime(2026, 10, 6, 8, 0, 0), Tz), until);
        // Güvenlik açısından kritik: SMS kapalı ve sessiz saatte de gider.
        Assert.Equal(Delivery.Send, DeliveryRules.DecideSms("signature.otp", Prefs(false, true), night).Decision);
    }

    [Fact]
    public void Gunluk_sinir_zamanlari_Istanbul_gunune_gore()
    {
        var now = QuietHoursCalc.ToUtc(new DateTime(2026, 10, 5, 1, 30, 0), Tz);
        Assert.Equal(QuietHoursCalc.ToUtc(new DateTime(2026, 10, 5), Tz), SmsSenderWorker.DayStartUtc(now));
        Assert.Equal(QuietHoursCalc.ToUtc(new DateTime(2026, 10, 6, 8, 0, 0), Tz), SmsSenderWorker.NextMorningUtc(now));
    }

    sealed class Capture(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Request = r;
            Body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Netgsm_istegi_ve_hata_kodlari()
    {
        var h = new Capture(HttpStatusCode.OK, """{"code":"00","jobid":"J1","description":"queued"}""");
        var r = await SmsProviders.SendAsync(new HttpClient(h), new SmsAccount("netgsm", "8501234567", "s3cret", "FIRMA"),
            new SmsMessage("905321234567", "Merhaba"), default);
        Assert.True(r.Ok);
        Assert.Equal("J1", r.Reference);
        Assert.EndsWith("/sms/rest/v2/send", h.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Basic", h.Request.Headers.Authorization!.Scheme);
        Assert.Equal("8501234567:s3cret", Encoding.UTF8.GetString(Convert.FromBase64String(h.Request.Headers.Authorization.Parameter!)));
        using var doc = JsonDocument.Parse(h.Body!);
        Assert.Equal("FIRMA", doc.RootElement.GetProperty("msgheader").GetString());
        Assert.Equal("5321234567", doc.RootElement.GetProperty("messages")[0].GetProperty("no").GetString());
        Assert.Equal("Merhaba", doc.RootElement.GetProperty("messages")[0].GetProperty("msg").GetString());

        var bad = await SmsProviders.SendAsync(new HttpClient(new Capture(HttpStatusCode.OK, """{"code":"30"}""")),
            new SmsAccount("netgsm", "u", "p", "F"), new SmsMessage("905321234567", "x"), default);
        Assert.False(bad.Ok);
        Assert.True(bad.Permanent);
        Assert.Contains("parola", bad.Error);
        var transient = await SmsProviders.SendAsync(new HttpClient(new Capture(HttpStatusCode.ServiceUnavailable, "")),
            new SmsAccount("netgsm", "u", "p", "F"), new SmsMessage("4915123456789", "x"), default);
        Assert.False(transient.Ok);
        Assert.False(transient.Permanent);
    }

    [Fact]
    public async Task Iletimerkezi_istegi_hmac_imzali()
    {
        var h = new Capture(HttpStatusCode.OK, """{"response":{"status":{"code":"200","message":"İşlem başarılı"},"order":{"id":"777"}}}""");
        var r = await SmsProviders.SendAsync(new HttpClient(h), new SmsAccount("iletimerkezi", "PUBKEY", "SECRET", "FIRMA"),
            new SmsMessage("905321234567", "Merhaba"), default);
        Assert.True(r.Ok);
        Assert.Equal("777", r.Reference);
        using var doc = JsonDocument.Parse(h.Body!);
        var req = doc.RootElement.GetProperty("request");
        Assert.Equal("PUBKEY", req.GetProperty("authentication").GetProperty("key").GetString());
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("SECRET"), Encoding.UTF8.GetBytes("PUBKEY"))).ToLowerInvariant();
        Assert.Equal(expected, req.GetProperty("authentication").GetProperty("hash").GetString());
        Assert.DoesNotContain("SECRET", h.Body);
        Assert.Equal("+905321234567", req.GetProperty("order").GetProperty("message").GetProperty("receipents").GetProperty("number")[0].GetString());

        var bad = await SmsProviders.SendAsync(new HttpClient(new Capture(HttpStatusCode.OK, """{"response":{"status":{"code":"401","message":"Üyelik bilgileri hatalı"}}}""")),
            new SmsAccount("iletimerkezi", "k", "s", "F"), new SmsMessage("905321234567", "x"), default);
        Assert.False(bad.Ok);
        Assert.True(bad.Permanent);
        Assert.Contains("401", bad.Error);
    }
}
