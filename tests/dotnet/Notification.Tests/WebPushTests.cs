using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationService.Push;
using Xunit;

namespace Notification.Tests;

public class WebPushTests
{
    static byte[] U(string s) => WebPushCrypto.UnB64(s);

    /// <summary>RFC 8291 Ek A test vektörü: aynı tuz ve sunucu anahtarıyla birebir aynı gövde.</summary>
    [Fact]
    public void RFC8291_test_vektoru()
    {
        var asPub = U("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var asKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = U("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = asPub[1..33], Y = asPub[33..65] },
        });
        var body = WebPushCrypto.Encrypt(
            U("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            U("BTBZMqHH6r4Tts7J_aSIgg"),
            U("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24"),
            U("DGv6ra1nlYgDCS1FRnbzlw"), asKey);
        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            WebPushCrypto.B64(body));
    }

    [Fact]
    public void VAPID_JWT_imzasi_dogrulanir()
    {
        var (pub, priv) = WebPushCrypto.NewVapidKeys();
        var jwt = WebPushCrypto.VapidJwt("https://fcm.googleapis.com/fcm/send/abc", pub, priv, "mailto:a@b.c", DateTimeOffset.UtcNow);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var claims = JsonDocument.Parse(U(parts[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        var q = U(pub);
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[1..33], Y = q[33..65] } });
        Assert.True(ec.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), U(parts[2]), HashAlgorithmName.SHA256));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/x", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", true)]
    [InlineData("https://web.push.apple.com/abc", true)]
    [InlineData("https://db5p.notify.windows.com/w/?token=x", true)]
    [InlineData("http://fcm.googleapis.com/x", false)]
    [InlineData("https://postgres:5432/x", false)]
    [InlineData("https://evil.example.com/fcm.googleapis.com", false)]
    [InlineData("https://fcm.googleapis.com.evil.com/x", false)]
    public void Uc_nokta_izin_listesi(string url, bool ok) => Assert.Equal(ok, PushEndpointPolicy.IsAllowed(url));
}
