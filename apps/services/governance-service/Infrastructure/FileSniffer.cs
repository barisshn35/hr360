using System.Text.Unicode;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Yüklenen dosyanın GERÇEK türünü ilk baytlarından (magic bytes) belirler ve istemcinin bildirdiği
/// Content-Type ile dosya uzantısını buna göre doğrular. İstemcinin söylediği türe güvenilmez:
/// ".png" adlı bir HTML/SVG, ".zip" adlı bir çalıştırılabilir dosya ya da "image/jpeg" diye
/// gönderilen bir PDF reddedilir. İzin verilen türler her çağrı yerinde ayrıca verilir.
///
/// Her servis bu sınıfın kendi kopyasını taşır (servisler ayrı Docker derleme bağlamı; ortak kütüphane
/// yok): learning, tenant, expense, governance. Birinde yapılan değişiklik diğerlerine de uygulanmalı.
///
/// Kötü amaçlı yazılım taraması (ör. ClamAV clamd INSTREAM) gerekirse yeri, çağrı yerinde bu
/// doğrulamanın hemen ARDINDAN ve dosya saklanmadan/işlenmeden öncesidir. Şimdilik eklenmedi
/// (geliştirme VM'inde bellek sınırlı); bkz. docs/guvenlik/README.md.
/// </summary>
public static class FileSniffer
{
    public const string Pdf = "application/pdf";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";
    /// <summary>Zip ve zip tabanlı biçimler (docx/xlsx/pptx, SCORM paketi).</summary>
    public const string Zip = "application/zip";
    /// <summary>Düz metin / CSV: NUL baytı içermeyen geçerli UTF-8.</summary>
    public const string Text = "text/plain";

    public enum Verdict { Ok, NotAllowed, Mismatch }

    /// <summary>Bildirilen tür ya da uzantı içerikle uyuşmadığında döndürülen ileti.</summary>
    public const string MismatchMessage = "Dosyanın içeriği, bildirilen dosya türü ya da uzantısıyla uyuşmuyor";

    private static readonly byte[] PngSig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ZipLocal = [(byte)'P', (byte)'K', 0x03, 0x04];
    private static readonly byte[] ZipEmpty = [(byte)'P', (byte)'K', 0x05, 0x06];

    /// <summary>İkili biçimi ilk baytlardan tanır; tanınmazsa null (metin ayrıca <see cref="IsUtf8Text"/> ile).</summary>
    public static string? Detect(ReadOnlySpan<byte> d)
    {
        if (d.StartsWith("%PDF-"u8)) return Pdf;
        if (d.StartsWith(PngSig)) return Png;
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return Jpeg;
        if (d.StartsWith("GIF87a"u8) || d.StartsWith("GIF89a"u8)) return Gif;
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8)) return Webp;
        if (d.StartsWith(ZipLocal) || d.StartsWith(ZipEmpty)) return Zip;
        return null;
    }

    /// <summary>Düz metin mi: boş değil, NUL baytı yok ve baştan sona geçerli UTF-8 (BOM serbest).</summary>
    public static bool IsUtf8Text(ReadOnlySpan<byte> d) => d.Length > 0 && d.IndexOf((byte)0) < 0 && Utf8.IsValid(d);

    /// <summary>
    /// İstemcinin bildirdiği Content-Type'ı karşılaştırılabilir türe indirger. Genel türler
    /// (boş, application/octet-stream) null döner: o durumda karar yalnızca içeriğe göre verilir.
    /// </summary>
    public static string? NormalizeDeclared(string? contentType)
    {
        var t = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return t switch
        {
            "" or "application/octet-stream" or "binary/octet-stream" or "application/unknown" => null,
            "image/jpg" or "image/pjpeg" => Jpeg,
            "application/x-pdf" => Pdf,
            "application/x-zip-compressed" or "application/x-zip" or "application/zip-compressed" or "multipart/x-zip" => Zip,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => Zip,
            "text/csv" or "application/csv" or "text/markdown" => Text,
            _ => t,
        };
    }

    /// <summary>Uzantının gerektirdiği tür; bilinmeyen ya da uzantısız adlarda null (denetlenmez).</summary>
    public static string? ExpectedForExtension(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".pdf" => Pdf,
            ".png" => Png,
            ".jpg" or ".jpeg" or ".jfif" => Jpeg,
            ".gif" => Gif,
            ".webp" => Webp,
            ".zip" or ".docx" or ".xlsx" or ".pptx" => Zip,
            ".txt" or ".csv" or ".md" => Text,
            _ => null,
        };

    /// <summary>
    /// İçeriği izin listesine göre doğrular. <paramref name="detected"/> içerikten belirlenen türdür
    /// (saklarken/iletirken istemcinin değil bunun kullanılması gerekir).
    /// NotAllowed: içerik tanınmadı ya da izinli değil. Mismatch: bildirilen tür ya da uzantı içerikle uyuşmuyor.
    /// </summary>
    public static Verdict Check(ReadOnlySpan<byte> data, string? declaredContentType, string? fileName,
        IReadOnlyCollection<string> allowed, out string? detected)
    {
        detected = Detect(data);
        if (detected is null && allowed.Contains(Text) && IsUtf8Text(data)) detected = Text;
        if (detected is null || !allowed.Contains(detected)) return Verdict.NotAllowed;
        var declared = NormalizeDeclared(declaredContentType);
        if (declared is not null && declared != detected) return Verdict.Mismatch;
        var byExtension = ExpectedForExtension(fileName);
        if (byExtension is not null && byExtension != detected) return Verdict.Mismatch;
        return Verdict.Ok;
    }
}
