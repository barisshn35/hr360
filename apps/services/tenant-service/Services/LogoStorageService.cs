using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TenantService.Services;

/// <summary>
/// Tenant logolarini MinIO'ya (S3-uyumlu) yukler. Her tenant'in tek bir
/// logo dosyasi vardir, tenant.Id ile adlandirilir - yeni yukleme ESKISININ
/// UZERINE YAZAR (bucket'ta biriken eski dosya kalmaz).
///
/// Yuklenen dosya, gateway'in "/logos/" yolundan (nginx, dogrudan MinIO'ya
/// proxy) HERKESE ACIK okunur - kimlik dogrulama gerektirmez, cunku logo
/// giris ekraninda (henuz oturum acilmadan) da gosterilir.
/// </summary>
public class LogoStorageService
{
    private const string BucketName = "tenant-logos";
    private const long MaxFileSizeBytes = 2 * 1024 * 1024; // 2 MB

    /// <summary>
    /// GUVENLIK: SVG artik KABUL EDILMIYOR. SVG betik (script) tasiyabilir ve logolar
    /// uygulamanin KENDI alan adindan (/logos/) sunuluyor - bir kiracinin yukledigi
    /// SVG, baglantiyi acan herkesin (baska kiracilarin kullanicilari, platform
    /// yoneticisi) oturumunda ayni kaynakta calisip Keycloak oturumundan jeton
    /// alabiliyordu. Ayrica tur, istemcinin gonderdigi ContentType'a degil dosyanin
    /// ilk baytlarina (magic bytes) bakilarak belirlenir (FileSniffer); bildirilen tur ya da
    /// uzanti icerikle uyusmazsa yukleme reddedilir.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedContentTypes = new()
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp",
    };

    /// <summary>Eski yuklemelerin temizligi icin (onceden svg kabul ediliyordu).</summary>
    private static readonly string[] AllKnownExtensions = { "png", "jpg", "webp", "svg" };

    private readonly AmazonS3Client _client;
    private readonly string _publicBaseUrl;

    public LogoStorageService()
    {
        var endpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT_URL")
            ?? "http://minio:9000";
        var accessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY")
            ?? throw new InvalidOperationException("MINIO_ACCESS_KEY tanimli olmali");
        var secretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY")
            ?? throw new InvalidOperationException("MINIO_SECRET_KEY tanimli olmali");
        _publicBaseUrl = (Environment.GetEnvironmentVariable("LOGO_PUBLIC_BASE_URL")
            ?? "http://localhost/logos").TrimEnd('/');

        _client = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint,
                ForcePathStyle = true, // MinIO virtual-hosted stili desteklemez.
                UseHttp = endpoint.StartsWith("http://"),
            });
    }

    public record UploadResult(bool Success, string? Url, string? Error);

    /// <summary>
    /// Veritabaninda saklanan logo adresini GUNCEL genel adrese cevirir. Adres yukleme
    /// aninda mutlak olarak saklaniyor; sonradan HTTPS acilinca ya da alan adi/port
    /// degisince eski kayitlar http://eski-adres/... olarak kaliyor ve tarayicida
    /// (karisik icerik) ya da e-postada kiriliyordu. Dosya adi (+?v= surumu) korunur,
    /// on ek her okumada LOGO_PUBLIC_BASE_URL'den uretilir.
    /// </summary>
    public static string? ToPublicUrl(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        var baseUrl = Environment.GetEnvironmentVariable("LOGO_PUBLIC_BASE_URL")?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) return stored;
        var i = stored.LastIndexOf('/');
        return i < 0 ? stored : baseUrl + stored[i..];
    }

    public async Task<UploadResult> UploadAsync(
        Guid tenantId, string? contentType, string? fileName, Stream fileStream, long fileSizeBytes, CancellationToken ct)
    {
        if (fileSizeBytes > MaxFileSizeBytes)
            return new UploadResult(false, null, "Dosya en fazla 2 MB olabilir");

        // Dosyayi bellege al (en fazla 2 MB) ve turunu iceriginden tespit et.
        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxFileSizeBytes)
            return new UploadResult(false, null, "Dosya en fazla 2 MB olabilir");
        var verdict = FileSniffer.Check(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), contentType, fileName,
            AllowedContentTypes.Keys, out var detected);
        if (verdict == FileSniffer.Verdict.Mismatch)
            return new UploadResult(false, null, FileSniffer.MismatchMessage);
        if (verdict != FileSniffer.Verdict.Ok || !AllowedContentTypes.TryGetValue(detected!, out var extension))
            return new UploadResult(false, null, "Sadece PNG, JPEG veya WebP kabul edilir");
        contentType = detected;
        buffer.Position = 0;

        var key = $"{tenantId}.{extension}";

        // Ayni tenant icin ONCEDEN farkli bir uzantiyla yuklenmis logo
        // varsa (ornek: once .png, simdi .svg) o eski dosyayi da temizle -
        // yoksa iki dosya birikir ve hangisinin "guncel" oldugu belirsizlesir.
        foreach (var oldExt in AllKnownExtensions)
        {
            if (oldExt == extension) continue;
            try
            {
                await _client.DeleteObjectAsync(BucketName, $"{tenantId}.{oldExt}", ct);
            }
            catch (AmazonS3Exception)
            {
                // Dosya yoksa MinIO hata vermez ama olur da baska bir sorun
                // cikarsa yuklemeyi engellememeli.
            }
        }

        try
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = BucketName,
                Key = key,
                InputStream = buffer,
                ContentType = contentType,
                AutoCloseStream = false,
            }, ct);
        }
        catch (Exception ex)
        {
            // Ic hata ayrintisi (MinIO adresi vb.) istemciye sizdirilmaz.
            Console.Error.WriteLine($"Logo yukleme hatasi: {ex}");
            return new UploadResult(false, null, "Yükleme başarısız, lütfen tekrar deneyin");
        }

        // Cache-busting: ayni dosya adiyla ustune yazildiginda tarayicinin
        // eski logoyu cache'den gostermemesi icin URL'e zaman damgasi eklenir.
        var url = $"{_publicBaseUrl}/{key}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return new UploadResult(true, url, null);
    }

    public async Task DeleteAsync(Guid tenantId, CancellationToken ct)
    {
        foreach (var ext in AllKnownExtensions)
        {
            try
            {
                await _client.DeleteObjectAsync(BucketName, $"{tenantId}.{ext}", ct);
            }
            catch (AmazonS3Exception)
            {
                // Dosya yoksa sorun degil.
            }
        }
    }
}
