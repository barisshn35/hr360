using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ExpenseService.Models;

namespace ExpenseService.Services;

/// <summary>
/// Y28 basit elektronik imza - expense tarafindaki saf (birim testli) kurallar: dokuman ozeti,
/// IP maskeleme ve ESKI kanit ozeti dogrulamasi. Kod (OTP) uretimi/dogrulamasi, imza ve yeni
/// kanitlar governance-service'teki tek imza motorundadir (<see cref="GovernanceSignatureClient"/>).
/// </summary>
public static class SimpleSignature
{
    /// <summary>Governance motorundaki belge turu adi.</summary>
    public const string DocumentType = "HrDocument";

    public const string Disclaimer =
        "Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir.";

    public static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>
    /// Dokuman ozeti. Dosya baytlari bu serviste tutulmadigi icin (MinIO'da, StorageKey ile)
    /// ozet dokumanin kanonik ust verisini kapsar: kimlik, calisan, tur, ad, depolama anahtari,
    /// boyut, icerik turu ve yukleme zamani (ms). Bunlardan biri degisirse ozet degisir.
    /// </summary>
    public static string DocumentHash(Document d)
    {
        var canonical = string.Join("\n",
            "hr360-document-v1",
            d.Id.ToString("D"),
            d.EmployeeId.ToString("D"),
            d.Type.ToString(),
            d.FileName,
            d.StorageKey ?? "",
            d.SizeBytes.ToString(CultureInfo.InvariantCulture),
            d.ContentType ?? "",
            d.UploadedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return Sha256Hex(canonical);
    }

    /// <summary>KVKK: IPv4'un son okteti, IPv6'nin /48 sonrasi sifirlanir. Gecersizse null.</summary>
    public static string? MaskIp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var first = raw.Split(',')[0].Trim();
        if (!IPAddress.TryParse(first, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return $"{b[0]}.{b[1]}.{b[2]}.0";
        for (var i = 6; i < b.Length; i++) b[i] = 0;
        return new IPAddress(b).ToString();
    }

    public static string? HashUserAgent(string? ua) =>
        string.IsNullOrWhiteSpace(ua) ? null : Sha256Hex(ua.Trim());

    /// <summary>ESKI kanit alanlarinin kanonik ozeti (butunluk kontrolu; kanit gosteriminde yeniden hesaplanir).</summary>
    public static string EvidenceHash(SignatureEvidence e) => Sha256Hex(string.Join("\n",
        "hr360-sign-evidence-v1",
        e.TenantSlug,
        e.SignatureId.ToString("D"),
        e.DocumentId.ToString("D"),
        e.SignerEmployeeId.ToString("D"),
        e.SignedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        e.DocumentHash,
        e.IpMasked ?? "",
        e.UserAgentHash ?? "",
        e.OtpChannel,
        e.Method));
}
