using GovernanceService.Security;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Entegrasyon sirlarini (Slack bot jetonu, Teams uygulama parolasi, OAuth yenileme
/// jetonlari) veritabaninda duz metin tutmamak icin AES-256-GCM. Anahtarlar
/// tenant-service ile ayni anahtar halkasidir (TENANT_SECRET_KEYS / TENANT_SECRET_KEY,
/// bkz. <see cref="KeyRing"/>). Bicim: eski anahtarla (k0) base64(nonce + tag + sifreli metin),
/// anahtar yenilendikten sonra "enc2:&lt;kimlik&gt;:" + base64. Acarken uc bicim de kabul edilir.
/// </summary>
public static class SecretBox
{
    private static void EnsureKey()
    {
        if (!KeyRing.Enabled)
            throw new InvalidOperationException((KeyRing.Error ?? "TENANT_SECRET_KEY tanımlı değil.") + " Entegrasyon sırları saklanamıyor.");
    }

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        EnsureKey();
        return KeyRing.Seal(plain, legacyPrefix: "");
    }

    public static string? Unprotect(string? sealedValue)
    {
        if (string.IsNullOrEmpty(sealedValue)) return null;
        EnsureKey();
        return KeyRing.Open(sealedValue);
    }
}
