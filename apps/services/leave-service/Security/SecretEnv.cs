using System.Data.Common;

namespace LeaveService.Security;

/// <summary>
/// Sırların ortam değişkeni yerine dosyadan (Docker secrets: /run/secrets/...) okunabilmesi.
/// Bilinen bir X değişkeni boşsa ve X_FILE tanımlıysa dosyanın içeriği süreç ortamına X olarak
/// yazılır; X doluysa hiçbir şey değişmez (varsayılan davranış birebir aynı). Veritabanı
/// bağlantı dizelerinde (X_DB_CONNECTION) parola sırası: X_DB_PASSWORD (bağlantıya özel, ör.
/// RETENTION_DB_PASSWORD), HR360_SERVICE_DB_PASSWORD (servise özel rol parolası; RETENTION_DB_CONNECTION
/// hariç), bunlar yoksa dizedeki parola, o da boşsa HR360_DB_PASSWORD. Program.cs'in ilk satırında çağrılır.
/// Servisler ayrı derlendiği için bu dosyanın birebir kopyası her serviste vardır
/// (apps/services/*/Security/SecretEnv.cs); değiştirirken hepsini güncelleyin.
/// </summary>
public static class SecretEnv
{
    /// <summary>X_FILE ile dosyadan okunabilen değişkenler (ayrıca adı _DB_CONNECTION / _DB_PASSWORD ile bitenler).</summary>
    public static readonly string[] Names =
    {
        "HR360_DB_PASSWORD", "HR360_SERVICE_DB_PASSWORD", "TENANT_SECRET_KEY", "TENANT_SECRET_KEYS",
        "INTERNAL_SERVICE_TOKEN", "INTERNAL_SERVICE_TOKEN_PREVIOUS", "KEYCLOAK_TENANT_ADMIN_CLIENT_SECRET",
        "KEYCLOAK_ADMIN_PASSWORD", "MINIO_ACCESS_KEY", "MINIO_SECRET_KEY", "SMTP_PASSWORD", "FORM_TOKEN_KEY",
        "LOGIN_WATCH_KEY", "SIEM_PSEUDONYM_KEY", "LLM_API_KEY", "OCR_CLIENT_SECRET",
    };

    private static bool Supported(string name) =>
        Array.IndexOf(Names, name) >= 0 || name.EndsWith("_DB_CONNECTION", StringComparison.Ordinal)
        || name.EndsWith("_DB_PASSWORD", StringComparison.Ordinal);

    /// <summary>Servis rolünün parolasının uygulanmadığı (başka rolle açılan) bağlantılar.</summary>
    private static readonly string[] OtherRoleConnections = { "RETENTION_DB_CONNECTION" };

    /// <summary>Süreç ortamına uygular; değişen değişkenlerin ADLARINI döner (değerler asla).</summary>
    public static IReadOnlyList<string> Load()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;
        var changed = Apply(env, File.ReadAllText);
        foreach (var name in changed) Environment.SetEnvironmentVariable(name, env[name]);
        return changed;
    }

    /// <summary>Saf hâli (testler için): <paramref name="env"/> yerinde güncellenir.</summary>
    public static List<string> Apply(IDictionary<string, string?> env, Func<string, string> readFile)
    {
        var changed = new List<string>();
        foreach (var key in env.Keys.ToList())
        {
            if (!key.EndsWith("_FILE", StringComparison.Ordinal)) continue;
            var name = key[..^"_FILE".Length];
            var path = env[key];
            if (!Supported(name) || string.IsNullOrWhiteSpace(path)) continue;
            if (env.TryGetValue(name, out var existing) && !string.IsNullOrEmpty(existing)) continue;
            string value;
            try { value = readFile(path).TrimEnd('\r', '\n'); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"{key} ile verilen dosya okunamadı ({path}): {ex.GetType().Name}");
            }
            if (value.Length == 0) throw new InvalidOperationException($"{key} ile verilen dosya boş ({path}).");
            env[name] = value;
            changed.Add(name);
        }

        string? Get(string n) => env.TryGetValue(n, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        var servicePassword = Get("HR360_SERVICE_DB_PASSWORD");
        var sharedPassword = Get("HR360_DB_PASSWORD");
        foreach (var key in env.Keys.ToList())
        {
            if (!key.EndsWith("_DB_CONNECTION", StringComparison.Ordinal) || string.IsNullOrEmpty(env[key])) continue;
            var own = Get(key[..^"_CONNECTION".Length] + "_PASSWORD");
            var forced = own ?? (Array.IndexOf(OtherRoleConnections, key) >= 0 ? null : servicePassword);
            var patched = WithPassword(env[key]!, forced, sharedPassword);
            if (patched is null) continue;
            env[key] = patched;
            if (!changed.Contains(key)) changed.Add(key);
        }
        return changed;
    }

    /// <summary>
    /// Bağlantı dizesinin parolasını ayarlar: <paramref name="forced"/> doluysa her zaman onu, değilse
    /// dizedeki parola boşken <paramref name="fallback"/>'i yazar. Değişiklik yoksa null.
    /// </summary>
    public static string? WithPassword(string connectionString, string? forced, string? fallback)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var current = b.TryGetValue("Password", out var cur) ? cur as string : null;
        string? next = !string.IsNullOrEmpty(forced) ? forced : string.IsNullOrEmpty(current) ? fallback : null;
        if (string.IsNullOrEmpty(next) || next == current) return null;
        b["Password"] = next;
        return b.ConnectionString;
    }
}
