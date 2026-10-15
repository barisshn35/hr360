using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace TenantService.Services;

/// <summary>
/// KVKK imha doğrulaması (Dalga 10, madde 57): silinen/anonimleştirilen kayıtların başvurduğu nesne
/// deposu (MinIO) dosyalarını siler ve yokluğunu doğrular. S3 kimlik bilgileri yalnızca bu serviste
/// olduğundan governance bu işi iç uçtan (X-Internal-Token) ister.
///
/// Yalnızca kişisel dosya kovaları (STORAGE_PERSONAL_BUCKETS, virgülle; varsayılan "hr360-documents")
/// silinebilir; şirket logosu ya da model dosyaları gibi kovalara bu yolla dokunulamaz. Anahtar
/// "kova/nesne" biçimindeyse ilk parça izinli bir kovaysa o kova, değilse varsayılan kova
/// (STORAGE_PERSONAL_BUCKET) kullanılır.
/// </summary>
public class ObjectStorageEraser
{
    private readonly AmazonS3Client? _client;
    private readonly string[] _buckets;
    private readonly string _defaultBucket;

    public ObjectStorageEraser()
    {
        _buckets = Buckets(Environment.GetEnvironmentVariable("STORAGE_PERSONAL_BUCKETS"));
        _defaultBucket = Environment.GetEnvironmentVariable("STORAGE_PERSONAL_BUCKET") is { Length: > 0 } b ? b.Trim() : _buckets[0];
        var endpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT_URL") ?? "http://minio:9000";
        var access = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY");
        var secret = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY");
        if (!string.IsNullOrEmpty(access) && !string.IsNullOrEmpty(secret))
            _client = new AmazonS3Client(new BasicAWSCredentials(access, secret),
                new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, UseHttp = endpoint.StartsWith("http://") });
    }

    public static string[] Buckets(string? env)
    {
        var list = (env ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => b.Length is >= 3 and <= 63).Distinct().ToArray();
        return list.Length > 0 ? list : new[] { "hr360-documents" };
    }

    /// <summary>(kova, nesne) — izinli değilse kova null. ".." ve mutlak yol reddedilir.</summary>
    public static (string? Bucket, string Key) Resolve(string raw, string[] allowed, string defaultBucket)
    {
        var k = (raw ?? "").Trim().TrimStart('/');
        if (k.Length == 0 || k.Length > 1024 || k.Contains("..") || k.Contains('\\')) return (null, k);
        if (k.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)) k = k[5..];
        var slash = k.IndexOf('/');
        if (slash > 0 && allowed.Contains(k[..slash])) return (k[..slash], k[(slash + 1)..]);
        if (slash > 0 && !allowed.Contains(k[..slash]) && LooksLikeForeignBucket(k[..slash])) return (null, k);
        return (allowed.Contains(defaultBucket) ? defaultBucket : null, k);
    }

    /// <summary>Bilinen ama kişisel olmayan kovalar (bu yolla silinemez).</summary>
    private static bool LooksLikeForeignBucket(string first) => first is "tenant-logos" or "hr360-mlflow-artifacts";

    public sealed record Result(string Key, string Status, string? Detail);

    /// <summary>Durumlar: Deleted (silindi ve yokluğu doğrulandı), Absent (zaten yok), NoBucket (kova yok — ürün bu dosyayı depolamıyor), NotAllowed, Failed.</summary>
    public async Task<List<Result>> DeleteAndVerifyAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var results = new List<Result>();
        var bucketExists = new Dictionary<string, bool>();
        foreach (var raw in keys.Distinct().Take(500))
        {
            var (bucket, key) = Resolve(raw, _buckets, _defaultBucket);
            if (bucket is null) { results.Add(new(raw, "NotAllowed", "İzinli kişisel dosya kovası değil")); continue; }
            if (_client is null) { results.Add(new(raw, "Failed", "Nesne deposu kimlik bilgisi yok")); continue; }
            try
            {
                if (!bucketExists.TryGetValue(bucket, out var hasBucket))
                    bucketExists[bucket] = hasBucket = await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_client, bucket);
                if (!hasBucket) { results.Add(new(raw, "NoBucket", $"'{bucket}' kovası yok")); continue; }
                var existed = await ExistsAsync(bucket, key, ct);
                if (existed == false) { results.Add(new(raw, "Absent", "Nesne zaten yok")); continue; }
                await _client.DeleteObjectAsync(bucket, key, ct);
                var still = await ExistsAsync(bucket, key, ct);
                results.Add(still == true ? new(raw, "Failed", "Silme sonrası nesne hâlâ duruyor") : new(raw, "Deleted", null));
            }
            catch (AmazonS3Exception ex)
            {
                results.Add(new(raw, "Failed", ex.ErrorCode ?? ex.StatusCode.ToString()));
            }
            catch (HttpRequestException)
            {
                results.Add(new(raw, "Failed", "Nesne deposuna ulaşılamadı"));
            }
        }
        return results;
    }

    private async Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            await _client!.GetObjectMetadataAsync(bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
