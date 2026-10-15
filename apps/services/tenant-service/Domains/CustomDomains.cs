using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsClient;

namespace TenantService.Domains;

/// <summary>G28: kiraciya ozel alan adi dogrulama kurallari (saf fonksiyonlar, birim testli).</summary>
public static class DomainValidator
{
    public const string TxtPrefix = "_hr360-verify.";

    private static readonly Regex Label = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly string[] ReservedSuffixes =
    {
        "localhost", "local", "internal", "intranet", "lan", "home.arpa", "invalid", "test", "onion", "corp", "hr360.local",
    };

    /// <summary>
    /// Girdiyi normallestirir (kucuk harf, sondaki nokta, IDN -> punycode). Hata varsa Turkce
    /// mesaj doner. <paramref name="platformHosts"/>: platformun kendi adresleri (ve alt alanlari)
    /// kiracilara verilemez (alt alan adi ele gecirme riski).
    /// </summary>
    public static (string? Domain, string? Error) Normalize(string? input, IEnumerable<string> platformHosts)
    {
        if (string.IsNullOrWhiteSpace(input)) return (null, "Alan adı zorunlu");
        var s = input.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Contains("://") || s.Contains('/') || s.Contains('?') || s.Contains('#') || s.Contains('@') || s.Contains(':'))
            return (null, "Yalnızca alan adını girin (örn. ik.sirket.com.tr) - http://, yol ya da port olmadan");
        if (s.StartsWith("*.")) return (null, "Joker (*) alan adları desteklenmiyor");
        string ascii;
        try { ascii = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }.GetAscii(s); }
        catch (ArgumentException) { return (null, "Alan adı geçersiz karakter içeriyor"); }
        if (ascii.Length > 253) return (null, "Alan adı en fazla 253 karakter olabilir");
        var labels = ascii.Split('.');
        if (labels.Length < 2) return (null, "Tam bir alan adı girin (örn. ik.sirket.com.tr)");
        if (labels.Any(l => !Label.IsMatch(l))) return (null, "Alan adı geçersiz (harf, rakam ve tire kullanılabilir)");
        var tld = labels[^1];
        if (!(Regex.IsMatch(tld, "^[a-z]{2,63}$") || tld.StartsWith("xn--"))) return (null, "Alan adının uzantısı geçersiz");
        foreach (var r in ReservedSuffixes)
            if (ascii == r || ascii.EndsWith("." + r)) return (null, "Bu alan adı ayrılmış/iç ağ alan adıdır, kullanılamaz");
        foreach (var p in platformHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()))
            if (ascii == p || ascii.EndsWith("." + p)) return (null, "Platformun kendi alan adı ya da alt alanı kullanılamaz");
        return (ascii, null);
    }

    /// <summary>Host basligi/sorgusundan (port ve sondaki nokta atilarak) alan adi; gecersizse null.</summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 260) return null;
        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('[')) return null; // IPv6 literal
        var colon = h.LastIndexOf(':');
        if (colon > 0) h = h[..colon];
        var (d, err) = Normalize(h, Array.Empty<string>());
        return err is null ? d : null;
    }

    public static string NewToken() => "hr360-verify=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string TxtName(string domain) => TxtPrefix + domain;

    /// <summary>TXT kayitlarindan biri jetonla birebir eslesiyor mu (bosluk/tirnak toleransli).</summary>
    public static bool Matches(IEnumerable<string> txtValues, string token) =>
        txtValues.Any(v => string.Equals(v.Trim().Trim('"').Trim(), token, StringComparison.Ordinal));

    /// <summary>PUBLIC_ORIGIN'deki platform adresi (kiracilara verilemez).</summary>
    public static IReadOnlyList<string> PlatformHostsFromEnv()
    {
        var list = new List<string>();
        foreach (var v in new[] { Environment.GetEnvironmentVariable("PUBLIC_ORIGIN"), Environment.GetEnvironmentVariable("PLATFORM_HOSTS") })
            foreach (var part in (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(Uri.TryCreate(part, UriKind.Absolute, out var u) ? u.Host : part);
        return list;
    }
}

/// <summary>Alan adi sahiplik kontrolu: _hr360-verify.&lt;alan adi&gt; TXT kaydi jetonla eslesmeli.</summary>
public static class DomainVerifier
{
    public sealed record Result(bool Verified, string? Error);

    public static async Task<Result> CheckAsync(ITxtResolver dns, string domain, string token, CancellationToken ct)
    {
        IReadOnlyList<string> values;
        try { values = await dns.ResolveTxtAsync(DomainValidator.TxtName(domain), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { values = Array.Empty<string>(); }
        if (DomainValidator.Matches(values, token)) return new(true, null);
        return new(false, values.Count == 0 ? "TXT kaydı bulunamadı" : "TXT kaydı doğrulama değeriyle eşleşmiyor");
    }
}

/// <summary>DNS TXT sorgusu - testte saptirilabilmesi icin arayuz.</summary>
public interface ITxtResolver
{
    Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct);
}

public sealed class DnsClientTxtResolver : ITxtResolver
{
    private static readonly LookupClient Client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 1,
        UseCache = false,
        ThrowDnsErrors = false,
    });

    public async Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct)
    {
        var result = await Client.QueryAsync(name, QueryType.TXT, QueryClass.IN, ct);
        if (result.HasError) return Array.Empty<string>();
        // Uzun TXT kayitlari birden fazla 255 karakterlik parcaya bolunur - birlestirilir.
        return result.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }
}

/// <summary>
/// YALNIZCA TEST: DNS_TXT_OVERRIDE_FILE'daki JSON ({"_hr360-verify.alan.com":["deger"]}) gercek DNS'ten
/// once okunur. Uretimde etkisizdir: yalnizca ASPNETCORE_ENVIRONMENT=Development ya da
/// TENANT_TEST_MODE=true (deploy/testing/chat-mock.yml) iken devreye girer.
/// </summary>
public sealed class OverridableTxtResolver : ITxtResolver
{
    private readonly ITxtResolver _inner;
    private readonly string? _file;
    private readonly ILogger<OverridableTxtResolver> _log;

    public OverridableTxtResolver(ILogger<OverridableTxtResolver> log)
    {
        _inner = new DnsClientTxtResolver();
        _log = log;
        _file = TestOverridesEnabled() ? Environment.GetEnvironmentVariable("DNS_TXT_OVERRIDE_FILE") : null;
        if (_file is not null) _log.LogWarning("TEST MODU: DNS TXT sorgulari {File} dosyasiyla saptirilabilir", _file);
    }

    public static bool TestOverridesEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("TENANT_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct)
    {
        if (_file is not null && File.Exists(_file))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string[]>>(await File.ReadAllTextAsync(_file, ct));
                if (map is not null && map.TryGetValue(name, out var values)) return values;
            }
            catch (JsonException) { _log.LogWarning("DNS saptirma dosyasi okunamadi"); }
        }
        return await _inner.ResolveTxtAsync(name, ct);
    }
}
