using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LearningService.Services;

public sealed record ScormEntry(string Path, string ContentType, byte[] Content);

public sealed record ScormParseResult(string? Error, string? Title, string? Identifier, string? EntryPoint, IReadOnlyList<ScormEntry> Files)
{
    public static ScormParseResult Fail(string error) => new(error, null, null, null, Array.Empty<ScormEntry>());
}

/// <summary>
/// Y20 SCORM 1.2 paket okuyucu. Zip kökünde imsmanifest.xml zorunlu; şema sürümü belirtilmişse
/// 1.2 olmalı; başlangıç SCO'su (adlcp:scormtype="sco") manifestten bulunur ve pakette var olmalı.
/// Güvenlik: yol geçişi (../, mutlak yol), zip bombası (dosya sayısı / açılmış boyut sınırı) ve
/// XML dış varlıkları (DTD kapalı) reddedilir.
/// </summary>
public static class ScormPackageReader
{
    public const long MaxUploadBytes = 50L * 1024 * 1024;
    public const long MaxExtractedBytes = 250L * 1024 * 1024;
    public const int MaxFiles = 5000;

    public static ScormParseResult Read(Stream zipStream)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { return ScormParseResult.Fail("Dosya geçerli bir zip arşivi değil"); }

        using (zip)
        {
            if (zip.Entries.Count > MaxFiles) return ScormParseResult.Fail($"Paket en fazla {MaxFiles} dosya içerebilir");
            var files = new List<ScormEntry>();
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue; // klasör
                var path = SafePath(e.FullName);
                if (path is null) return ScormParseResult.Fail($"Geçersiz dosya yolu: {e.FullName}");
                total += e.Length;
                if (e.Length > int.MaxValue || total > MaxExtractedBytes)
                    return ScormParseResult.Fail("Paketin açılmış boyutu çok büyük");
                using var s = e.Open();
                using var ms = new MemoryStream((int)Math.Min(e.Length, 16 * 1024 * 1024));
                // Bildirilen boyuta güvenmeden sınırlı okuma (sahte başlıklı zip bombası).
                var buf = new byte[81920];
                int n; long read = 0;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    read += n;
                    if (read > e.Length + 1024 || total - e.Length + read > MaxExtractedBytes)
                        return ScormParseResult.Fail("Paketin açılmış boyutu çok büyük");
                    ms.Write(buf, 0, n);
                }
                if (files.Any(f => string.Equals(f.Path, path, StringComparison.Ordinal)))
                    return ScormParseResult.Fail($"Pakette yinelenen dosya: {path}");
                files.Add(new ScormEntry(path, ContentTypeFor(path), ms.ToArray()));
            }

            var manifest = files.FirstOrDefault(f => f.Path == "imsmanifest.xml");
            if (manifest is null)
                return ScormParseResult.Fail("imsmanifest.xml paketin kök klasöründe bulunamadı (SCORM 1.2 paketi değil)");

            XDocument doc;
            try
            {
                using var ms = new MemoryStream(manifest.Content);
                using var xr = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                doc = XDocument.Load(xr);
            }
            catch (XmlException) { return ScormParseResult.Fail("imsmanifest.xml okunamadı (geçersiz XML)"); }

            var root = doc.Root!;
            if (root.Name.LocalName != "manifest") return ScormParseResult.Fail("imsmanifest.xml kök öğesi 'manifest' olmalı");
            var schemaVersion = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "schemaversion")?.Value.Trim();
            if (!string.IsNullOrEmpty(schemaVersion) && !schemaVersion.Contains("1.2"))
                return ScormParseResult.Fail($"Yalnızca SCORM 1.2 paketleri desteklenir (paket: {schemaVersion})");

            var resources = root.Descendants().Where(x => x.Name.LocalName == "resource").ToList();
            var resourcesBase = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "resources")?.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "base")?.Value ?? "";
            bool IsSco(XElement r) => string.Equals(
                r.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("scormtype", StringComparison.OrdinalIgnoreCase))?.Value,
                "sco", StringComparison.OrdinalIgnoreCase);
            string? Href(XElement r) => r.Attribute("href")?.Value;

            // Varsayılan organizasyonun ilk öğesinin gösterdiği kaynak; yoksa ilk SCO.
            XElement? chosen = null;
            var orgs = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "organizations");
            var defaultOrgId = orgs?.Attribute("default")?.Value;
            var org = orgs?.Elements().FirstOrDefault(o => o.Name.LocalName == "organization" && (defaultOrgId == null || o.Attribute("identifier")?.Value == defaultOrgId))
                      ?? orgs?.Elements().FirstOrDefault(o => o.Name.LocalName == "organization");
            var title = org?.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value.Trim();
            var firstRef = org?.Descendants().Where(x => x.Name.LocalName == "item")
                .Select(i => i.Attribute("identifierref")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (firstRef is not null)
                chosen = resources.FirstOrDefault(r => r.Attribute("identifier")?.Value == firstRef && Href(r) is not null);
            chosen ??= resources.FirstOrDefault(r => IsSco(r) && Href(r) is not null);
            if (chosen is null) return ScormParseResult.Fail("Manifestte başlatılabilir bir SCO kaynağı (href) bulunamadı");

            var resBase = chosen.Attributes().FirstOrDefault(a => a.Name.LocalName == "base")?.Value ?? "";
            var href = resourcesBase + resBase + Href(chosen)!;
            var hrefPath = href.Split('?', '#')[0];
            var entryPath = SafePath(Uri.UnescapeDataString(hrefPath));
            if (entryPath is null || !files.Any(f => f.Path == entryPath))
                return ScormParseResult.Fail($"Başlangıç dosyası pakette yok: {hrefPath}");
            var entry = entryPath + href[hrefPath.Length..];

            return new ScormParseResult(null, string.IsNullOrWhiteSpace(title) ? null : title,
                root.Attribute("identifier")?.Value, entry, files);
        }
    }

    /// <summary>Zip içi yolu normalleştirir; kök dışına çıkan ya da mutlak yolları reddeder (null).</summary>
    public static string? SafePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = raw.Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':') || p.Contains('\0')) return null;
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") return null;
            parts.Add(seg);
        }
        var result = string.Join('/', parts);
        return result.Length is 0 or > 480 ? null : result;
    }

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json", [".xml"] = "application/xml",
        [".xsd"] = "application/xml", [".dtd"] = "application/xml-dtd", [".txt"] = "text/plain; charset=utf-8",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml", [".webp"] = "image/webp", [".ico"] = "image/x-icon", [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".ogg"] = "audio/ogg", [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav", [".m4a"] = "audio/mp4", [".vtt"] = "text/vtt",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".eot"] = "application/vnd.ms-fontobject", [".pdf"] = "application/pdf", [".swf"] = "application/x-shockwave-flash",
    };

    public static string ContentTypeFor(string path) =>
        Types.TryGetValue(Path.GetExtension(path), out var t) ? t : "application/octet-stream";
}

/// <summary>
/// SCORM içeriği iframe içinde açılır; iframe istekleri Authorization başlığı taşıyamaz. Başlatma
/// ucu, pakete ve çalışana bağlı kısa ömürlü imzalı bir jeton üretir ve yalnızca o paketin dosya
/// yoluna kapsamlı HttpOnly + SameSite=Strict çerez olarak yazar. Anahtar TENANT_SECRET_KEY'den
/// türetilir (yoksa süreç başına rastgele — yeniden başlatmada jetonlar geçersizleşir).
/// </summary>
public static class ScormLaunchTokens
{
    public const string CookieName = "hr360_scorm";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);
    private static readonly byte[] Key = DeriveKey();

    private static byte[] DeriveKey()
    {
        var secret = Environment.GetEnvironmentVariable("TENANT_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(secret)) return RandomNumberGenerator.GetBytes(32);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("hr360-scorm-launch-v1"));
    }

    public static string Issue(string tenant, Guid packageId, Guid employeeId, DateTimeOffset now)
    {
        var payload = $"v1|{tenant}|{packageId:N}|{employeeId:N}|{now.Add(Lifetime).ToUnixTimeSeconds()}";
        var p = Encoding.UTF8.GetBytes(payload);
        return B64(p) + "." + B64(HMACSHA256.HashData(Key, p));
    }

    /// <summary>Geçerliyse (kiracı, çalışan) döner; imza, süre ve paket eşleşmesi kontrol edilir.</summary>
    public static (string Tenant, Guid EmployeeId)? Validate(string? token, Guid packageId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0) return null;
        byte[] p, sig;
        try { p = UnB64(token[..dot]); sig = UnB64(token[(dot + 1)..]); }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(sig, HMACSHA256.HashData(Key, p))) return null;
        var parts = Encoding.UTF8.GetString(p).Split('|');
        if (parts.Length != 5 || parts[0] != "v1") return null;
        if (!Guid.TryParse(parts[2], out var pkg) || pkg != packageId) return null;
        if (!Guid.TryParse(parts[3], out var emp)) return null;
        if (!long.TryParse(parts[4], out var exp) || now.ToUnixTimeSeconds() > exp) return null;
        return (parts[1], emp);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
