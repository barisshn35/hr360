namespace NotificationService.Security;

/// <summary>
/// tenant-service/Security/SmtpCredentialProtector.cs ile BIREBIR AYNI -
/// kiracinin AES-256-GCM ile sifrelenmis SMTP parolasini cozmek icin
/// burada da bulunmasi gerekiyor (iki servis de ayni TENANT_SECRET_KEY
/// ortam degiskenini paylasir). Kod tekrari bilincli: bu monorepo'da
/// servisler arasinda paylasilan bir ortak kutuphane yok (her servis
/// kendi Dockerfile'iyla bagimsiz build edilir), o yuzden kucuk,
/// bagimsiz sinifları kopyalamak, cross-service paket bagimliligi
/// eklemekten daha basit ve servisleri birbirinden ayri tutuyor.
/// Anahtar halkasi (TENANT_SECRET_KEYS, enc2 bicimi) icin bkz. KeyRing.
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
