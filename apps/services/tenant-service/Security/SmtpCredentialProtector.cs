namespace TenantService.Security;

/// <summary>
/// Tenant'in kendi SMTP sifresini DB'de duz metin TUTMAMAK icin AES-256-GCM
/// ile sifreler/cozer.
///
/// KRITIK: ASP.NET Core Data Protection API BILEREK KULLANILMADI - o
/// anahtarlarini container'in kendi (ephemeral) dosya sistemine yazar,
/// her "docker compose" yeniden olusturmasinda (yani HER DEPLOY'DA)
/// anahtarlar kaybolur ve onceden sifrelenmis veriler COZULEMEZ hale
/// gelir. Burada TENANT_SECRET_KEY sabit bir ortam degiskeninden
/// okunuyor - deploy'lar arasi AYNI KEY kullanildigi icin kalici.
/// Anahtar yenileme (TENANT_SECRET_KEYS) icin bkz. KeyRing: eski anahtarla (k0) bicim
/// base64(nonce + tag + sifreli metin), yenilendikten sonra "enc2:<kimlik>:" + base64.
/// </summary>
public class SmtpCredentialProtector
{
    public SmtpCredentialProtector()
    {
        // Eskiden oldugu gibi: anahtar yoksa servis acilisinda (DI) hata verir.
        if (!KeyRing.Enabled)
            throw new InvalidOperationException(KeyRing.Error ?? "TENANT_SECRET_KEY tanimli olmali");
    }

    /// <summary>AES-256-GCM: etkin anahtar k0 ise base64(nonce | etiket | sifreli metin), degilse "enc2:kimlik:" + base64.</summary>
    public string Encrypt(string plainText) => KeyRing.Seal(plainText, legacyPrefix: "");

    public string Decrypt(string encryptedBase64) => KeyRing.Open(encryptedBase64);
}
