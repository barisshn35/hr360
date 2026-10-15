using System.Security.Cryptography;
using System.Text;
using CompensationService.Security;
using Xunit;

namespace Compensation.Tests;

/// <summary>
/// Anahtar halkası biçim uyumu (Security/KeyRing.cs, her serviste aynı kopya): eski enc1 / öneksiz
/// değerler k0 ile açılır, yeni anahtar etkinken enc2:&lt;kimlik&gt;: yazılır, yeniden şifreleme kararları.
/// TENANT_SECRET_KEY(S) süreç genelinde değiştirildiği için paralel koşmaz.
/// </summary>
[Collection("TenantSecretKey")]
public class KeyRingTests
{
    private static readonly string Old = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string New = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private sealed class Env : IDisposable
    {
        private readonly string? _keys = Environment.GetEnvironmentVariable("TENANT_SECRET_KEYS");
        private readonly string? _key = Environment.GetEnvironmentVariable("TENANT_SECRET_KEY");
        public Env(string? key, string? keys)
        {
            Environment.SetEnvironmentVariable("TENANT_SECRET_KEY", key);
            Environment.SetEnvironmentVariable("TENANT_SECRET_KEYS", keys);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TENANT_SECRET_KEY", _key);
            Environment.SetEnvironmentVariable("TENANT_SECRET_KEYS", _keys);
        }
    }

    /// <summary>Bu dalgadan önceki kodun yazdığı biçim: base64(nonce12 | etiket16 | şifreli).</summary>
    private static string LegacyRaw(string keyB64, string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var p = Encoding.UTF8.GetBytes(plain);
        var c = new byte[p.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(keyB64), 16);
        aes.Encrypt(nonce, p, c, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. c]);
    }

    [Fact]
    public void Tek_anahtarla_eski_bicim_yazilir_ve_okunur()
    {
        using var _ = new Env(Old, null);
        Assert.True(KeyRing.Enabled);
        Assert.Equal("k0", KeyRing.ActiveId);
        var v1 = KeyRing.Seal("TR330006100519786457841326");
        Assert.StartsWith("enc1:", v1);
        Assert.Equal("TR330006100519786457841326", KeyRing.Open(v1));
        var raw = KeyRing.Seal("xoxb-jeton", legacyPrefix: "");
        Assert.False(KeyRing.IsSealed(raw));
        Assert.Equal("xoxb-jeton", KeyRing.Open(raw));
        // Eski kodun ürettiği değerler değişmeden açılır.
        Assert.Equal("eski", KeyRing.Open(LegacyRaw(Old, "eski")));
        Assert.Equal("eski", KeyRing.Open("enc1:" + LegacyRaw(Old, "eski")));
    }

    [Fact]
    public void Yeni_anahtar_etkinken_enc2_yazilir_eski_bicimler_acilir()
    {
        using var _ = new Env(Old, $"k1:{New}, k0:{Old}");
        Assert.Equal("k1", KeyRing.ActiveId);
        var v = KeyRing.Seal("12345678901");
        Assert.StartsWith("enc2:k1:", v);
        Assert.Equal("12345678901", KeyRing.Open(v));
        Assert.StartsWith("enc2:k1:", KeyRing.Seal("x", legacyPrefix: ""));
        Assert.Equal("a", KeyRing.Open(LegacyRaw(Old, "a")));
        Assert.Equal("b", KeyRing.Open("enc1:" + LegacyRaw(Old, "b")));
        Assert.Equal("c", KeyRing.Open("enc2:k0:" + LegacyRaw(Old, "c")));
        Assert.Equal("k1", KeyRing.KeyIdOf(v, rawIsSealed: false));
        Assert.Equal("k0", KeyRing.KeyIdOf(LegacyRaw(Old, "a"), rawIsSealed: true));
        Assert.Null(KeyRing.KeyIdOf("düz metin", rawIsSealed: false));
    }

    [Fact]
    public void Eski_anahtar_cikarilinca_eski_degerler_acilmaz()
    {
        string v2, v1;
        using (new Env(Old, $"k1:{New},k0:{Old}")) { v2 = KeyRing.Seal("yeni"); v1 = "enc1:" + LegacyRaw(Old, "eski"); }
        using var _ = new Env(Old, $"k1:{New}");
        Assert.Equal("yeni", KeyRing.Open(v2));
        Assert.ThrowsAny<CryptographicException>(() => KeyRing.Open(v1));
        Assert.ThrowsAny<CryptographicException>(() => KeyRing.Open("enc2:k9:" + LegacyRaw(New, "x")));
    }

    [Fact]
    public void Kurcalanan_deger_reddedilir()
    {
        using var _ = new Env(null, $"k1:{New}");
        var v = KeyRing.Seal("gizli");
        var b = Convert.FromBase64String(v["enc2:k1:".Length..]);
        b[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => KeyRing.Open("enc2:k1:" + Convert.ToBase64String(b)));
    }

    [Fact]
    public void Gecersiz_anahtar_listesi_halkayi_kapatir_ve_degeri_yazmaz()
    {
        using var _ = new Env(Old, "k1:kisa,k0:" + Old);
        Assert.False(KeyRing.Enabled);
        Assert.NotNull(KeyRing.Error);
        Assert.DoesNotContain(Old, KeyRing.Error!);
        Assert.Throws<InvalidOperationException>(() => KeyRing.Seal("x"));
        using var __ = new Env(null, null);
        Assert.False(KeyRing.Enabled);
    }

    [Fact]
    public void Bayt_dizisi_eski_ve_yeni_bicim()
    {
        var plain = Encoding.UTF8.GetBytes("dosya içeriği");
        byte[] legacy;
        using (new Env(Old, null)) { legacy = KeyRing.SealBytes(plain); Assert.Equal(plain.Length + 28, legacy.Length); }
        using var _ = new Env(Old, $"k1:{New},k0:{Old}");
        var current = KeyRing.SealBytes(plain);
        Assert.Equal("HRE2"u8.ToArray(), current[..4]);
        Assert.Equal("k1", KeyRing.BytesKeyId(current));
        Assert.Equal("k0", KeyRing.BytesKeyId(legacy));
        Assert.Equal(plain, KeyRing.OpenBytes(current));
        Assert.Equal(plain, KeyRing.OpenBytes(legacy));
        Assert.Null(KeyRotationJob.Reencrypt(current));
        var re = KeyRotationJob.Reencrypt(legacy)!;
        Assert.Equal("k1", KeyRing.BytesKeyId(re));
        Assert.Equal(plain, KeyRing.OpenBytes(re));
    }

    [Fact]
    public void Yeniden_sifreleme_kararlari()
    {
        string legacyRaw = LegacyRaw(Old, "jeton"), legacyV1 = "enc1:" + LegacyRaw(Old, "TCKN");
        using (new Env(Old, null))
        {
            // Yalnızca k0 varken eski biçimler günceldir.
            Assert.Null(KeyRotationJob.Reencrypt(legacyRaw, EncKind.Text));
            Assert.Null(KeyRotationJob.Reencrypt(legacyV1, EncKind.Prefixed));
        }
        using var _ = new Env(Old, $"k1:{New},k0:{Old}");
        var t = KeyRotationJob.Reencrypt(legacyRaw, EncKind.Text)!;
        Assert.StartsWith("enc2:k1:", t);
        Assert.Equal("jeton", KeyRing.Open(t));
        var p = KeyRotationJob.Reencrypt(legacyV1, EncKind.Prefixed)!;
        Assert.StartsWith("enc2:k1:", p);
        Assert.Equal("TCKN", KeyRing.Open(p));
        Assert.Null(KeyRotationJob.Reencrypt(p, EncKind.Prefixed));          // zaten etkin anahtarla
        Assert.Null(KeyRotationJob.Reencrypt("düz IBAN", EncKind.Prefixed)); // düz metne dokunulmaz (PiiBackfill'in işi)
    }

    [Fact]
    public void Geri_donuste_k0_etkinse_enc2_degerler_eski_bicime_doner()
    {
        string v;
        using (new Env(Old, $"k1:{New},k0:{Old}")) v = KeyRing.Seal("x");
        using var _ = new Env(Old, $"k0:{Old},k1:{New}");
        Assert.Equal("x", KeyRing.Open(v));
        var back = KeyRotationJob.Reencrypt(v, EncKind.Prefixed)!;
        Assert.StartsWith("enc1:", back);
        Assert.Equal("x", KeyRing.Open(back));
    }
}
