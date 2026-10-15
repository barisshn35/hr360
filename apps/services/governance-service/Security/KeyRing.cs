using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GovernanceService.Security;

/// <summary>
/// Şifreli alanların anahtar halkası (AES-256-GCM, ek veri yok; yerleşim nonce(12) | etiket(16) | şifreli metin).
///
/// Anahtarlar: TENANT_SECRET_KEYS="kimlik1:base64,kimlik2:base64" (ilk = etkin; tanımlıysa tek kaynak budur).
/// Tanımlı değilse tek anahtar vardır: "k0" = TENANT_SECRET_KEY (eski kurulumlar değişmeden çalışır).
/// Değer biçimleri:
///   enc2:&lt;kimlik&gt;:&lt;base64&gt;  — yeni biçim, anahtar kimliği değerin içinde.
///   enc1:&lt;base64&gt;            — eski biçim, k0 ile açılır (TCKN/IBAN, pasaport, özel alanlar).
///   &lt;base64&gt; (öneksiz)        — eski biçim, k0 ile açılır (entegrasyon sırları, SMTP/LDAP parolası).
///   bayt dizisi: "HRE2" + kimlik uzunluğu (1 bayt) + kimlik + yerleşim; öneksiz = eski, k0.
/// Yazarken etkin anahtar k0 ise eski biçim yazılır (geri dönüş ve eski sürümlerle uyum için), değilse enc2.
/// Etkin olmayan anahtarla şifrelenmiş satırları <see cref="KeyRotationJob"/> etkin anahtarla yeniden yazar.
/// Servisler ayrı derlendiği için bu dosyanın kopyası şifreli alan kullanan her serviste vardır
/// (engagement, governance, tenant, notification, expense, compensation); değiştirirken hepsini güncelleyin.
/// </summary>
public static class KeyRing
{
    public const string LegacyId = "k0";
    public const string V1 = "enc1:";
    public const string V2 = "enc2:";
    private const int N = 12, T = 16;
    private static readonly byte[] Magic = "HRE2"u8.ToArray();
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    private sealed record State(string? Keys, string? Key, string? ActiveId, Dictionary<string, byte[]> Ring, string? Error);
    private static State? _state;

    /// <summary>Ortam değiştiyse (testler, *_FILE yüklemesi) halkayı yeniden kurar.</summary>
    private static State Current
    {
        get
        {
            var keys = Environment.GetEnvironmentVariable("TENANT_SECRET_KEYS");
            var key = Environment.GetEnvironmentVariable("TENANT_SECRET_KEY");
            var s = _state;
            if (s is not null && s.Keys == keys && s.Key == key) return s;
            return _state = Build(keys, key);
        }
    }

    private static State Build(string? keys, string? key)
    {
        var ring = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(keys))
        {
            string? active = null;
            foreach (var raw in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = raw.IndexOf(':');
                var id = i > 0 ? raw[..i] : "";
                if (!IdPattern.IsMatch(id)) return new(keys, key, null, ring, "TENANT_SECRET_KEYS: geçersiz anahtar kimliği (biçim: kimlik:base64, kimlikte harf/rakam/_/-).");
                byte[] k;
                try { k = Convert.FromBase64String(raw[(i + 1)..]); }
                catch (FormatException) { return new(keys, key, null, ring, $"TENANT_SECRET_KEYS: '{id}' anahtarı base64 değil."); }
                if (k.Length != 32) return new(keys, key, null, ring, $"TENANT_SECRET_KEYS: '{id}' anahtarı 32 bayt olmalı.");
                if (!ring.TryAdd(id, k)) return new(keys, key, null, ring, $"TENANT_SECRET_KEYS: '{id}' kimliği iki kez geçiyor.");
                active ??= id;
            }
            return active is null ? new(keys, key, null, ring, "TENANT_SECRET_KEYS boş.") : new(keys, key, active, ring, null);
        }
        if (string.IsNullOrWhiteSpace(key)) return new(keys, key, null, ring, "TENANT_SECRET_KEY tanımlı değil.");
        try
        {
            var k = Convert.FromBase64String(key);
            if (k.Length != 32) return new(keys, key, null, ring, "TENANT_SECRET_KEY 32 bayt (base64) olmalı.");
            ring[LegacyId] = k;
            return new(keys, key, LegacyId, ring, null);
        }
        catch (FormatException) { return new(keys, key, null, ring, "TENANT_SECRET_KEY base64 değil."); }
    }

    public static bool Enabled => Current.ActiveId is not null;
    public static string? ActiveId => Current.ActiveId;
    /// <summary>Halka kurulamadıysa nedeni (anahtar değeri içermez).</summary>
    public static string? Error => Current.Error;
    public static IReadOnlyCollection<string> KeyIds => Current.Ring.Keys;

    private static byte[] KeyOrThrow(State s, string id) =>
        s.Ring.TryGetValue(id, out var k) ? k
        : throw new CryptographicException(s.ActiveId is null ? (s.Error ?? "Şifreleme anahtarı yok.") : $"'{id}' kimlikli anahtar halkada yok (TENANT_SECRET_KEYS).");

    private static State Ready()
    {
        var s = Current;
        if (s.ActiveId is null) throw new InvalidOperationException(s.Error ?? "Şifreleme anahtarı tanımlı değil.");
        return s;
    }

    private static byte[] Encrypt(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(N);
        var tag = new byte[T];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, T);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    private static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> data)
    {
        if (data.Length < N + T) throw new CryptographicException("Şifreli değer kısa.");
        var plain = new byte[data.Length - N - T];
        using var aes = new AesGcm(key, T);
        aes.Decrypt(data[..N], data[(N + T)..], data.Slice(N, T), plain);
        return plain;
    }

    /// <summary>Metni etkin anahtarla şifreler. Etkin anahtar k0 ise eski biçim: <paramref name="legacyPrefix"/> + base64.</summary>
    public static string Seal(string plain, string legacyPrefix = V1)
    {
        var s = Ready();
        var b64 = Convert.ToBase64String(Encrypt(s.Ring[s.ActiveId!], Encoding.UTF8.GetBytes(plain)));
        return s.ActiveId == LegacyId ? legacyPrefix + b64 : V2 + s.ActiveId + ":" + b64;
    }

    /// <summary>enc2 / enc1 / öneksiz base64 değeri açar. Anahtar yoksa ya da değer bozuksa hata atar.</summary>
    public static string Open(string stored)
    {
        var s = Ready();
        var (id, b64) = Split(stored);
        return Encoding.UTF8.GetString(Decrypt(KeyOrThrow(s, id), Convert.FromBase64String(b64)));
    }

    /// <summary>Değer enc1:/enc2: önekli mi (öneksiz değerin düz metin olabildiği alanlar için).</summary>
    public static bool IsSealed(string? value) =>
        value is not null && (value.StartsWith(V1, StringComparison.Ordinal) || value.StartsWith(V2, StringComparison.Ordinal));

    /// <summary>Değerin anahtar kimliği; öneksiz değer <paramref name="rawIsSealed"/> ise k0, değilse null (düz metin).</summary>
    public static string? KeyIdOf(string? stored, bool rawIsSealed)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!IsSealed(stored) && !rawIsSealed) return null;
        try { return Split(stored).Id; } catch (FormatException) { return null; }
    }

    private static (string Id, string B64) Split(string stored)
    {
        if (stored.StartsWith(V2, StringComparison.Ordinal))
        {
            var i = stored.IndexOf(':', V2.Length);
            if (i < 0) throw new FormatException("enc2 değerinde anahtar kimliği yok.");
            return (stored[V2.Length..i], stored[(i + 1)..]);
        }
        if (stored.StartsWith(V1, StringComparison.Ordinal)) return (LegacyId, stored[V1.Length..]);
        return (LegacyId, stored);
    }

    /// <summary>Bayt dizisini etkin anahtarla şifreler (k0 ise eski, başlıksız biçim).</summary>
    public static byte[] SealBytes(byte[] plain)
    {
        var s = Ready();
        var body = Encrypt(s.Ring[s.ActiveId!], plain);
        if (s.ActiveId == LegacyId) return body;
        var id = Encoding.ASCII.GetBytes(s.ActiveId!);
        return [.. Magic, (byte)id.Length, .. id, .. body];
    }

    public static byte[] OpenBytes(byte[] data)
    {
        var s = Ready();
        var id = HeaderId(data);
        if (id is not null && s.Ring.TryGetValue(id, out var k))
        {
            try { return Decrypt(k, data.AsSpan(Magic.Length + 1 + id.Length)); }
            catch (CryptographicException) when (s.Ring.ContainsKey(LegacyId)) { /* başlık rastlantısal olabilir: eski biçimi dene */ }
        }
        return Decrypt(KeyOrThrow(s, LegacyId), data);
    }

    /// <summary>Bayt dizisinin başlığındaki anahtar kimliği; başlıksızsa k0.</summary>
    public static string BytesKeyId(byte[] data) => HeaderId(data) ?? LegacyId;

    private static string? HeaderId(byte[] data)
    {
        if (data.Length < Magic.Length + 1 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        int len = data[Magic.Length];
        if (len == 0 || data.Length < Magic.Length + 1 + len + N + T) return null;
        var id = Encoding.ASCII.GetString(data, Magic.Length + 1, len);
        return IdPattern.IsMatch(id) ? id : null;
    }
}
