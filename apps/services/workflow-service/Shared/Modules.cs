// OTOMATIK KOPYA: kaynak apps/shared-dotnet (scripts/sync-shared.sh). Bu dosyayi duzenlemeyin.
namespace HR360.Platform;

/// <summary>
/// Kurulumda açık isteğe bağlı modüller (scripts/lib/modules.sh) ve kiracılık kipi.
/// <c>HR360_MODULES</c>: "all" ya da virgüllü kimlik listesi; yok/boş = hepsi açık (eski kurulumlar),
/// bilinmeyen kimlik yok sayılır. <c>HR360_TENANCY</c>: "single" | "multi"; yoksa "multi".
/// Ortam bir kez okunur. Çekirdek alanlar (All dışındaki kimlikler) her zaman açık sayılır.
/// Birim testi ortamı değiştirmez, <see cref="Override"/> kullanır (akışa bağlı, testler arası sızmaz).
/// </summary>
public static class Modules
{
    public const string Recruitment = "recruitment";
    public const string Onboarding = "onboarding";
    public const string TimeShift = "timeshift";
    public const string Performance = "performance";
    public const string Learning = "learning";
    public const string Compensation = "compensation";
    public const string Ml = "ml";
    public const string Expense = "expense";

    public static readonly IReadOnlyList<string> All =
        [Recruitment, Onboarding, TimeShift, Performance, Learning, Compensation, Ml, Expense];

    private sealed record State(IReadOnlySet<string> Enabled, string Tenancy);

    private static readonly Lazy<State> FromEnv = new(() => new State(
        ParseModules(Environment.GetEnvironmentVariable("HR360_MODULES")),
        ParseTenancy(Environment.GetEnvironmentVariable("HR360_TENANCY"))));

    private static readonly AsyncLocal<State?> TestState = new();

    private static State Current => TestState.Value ?? FromEnv.Value;

    /// <summary>Açık isteğe bağlı modüllerin kimlikleri (hiçbir zaman "all" içermez).</summary>
    public static IReadOnlySet<string> Enabled => Current.Enabled;

    /// <summary>"single" ya da "multi".</summary>
    public static string Tenancy => Current.Tenancy;

    public static bool IsSingleTenant => Tenancy == "single";

    public static bool IsEnabled(string id) =>
        !All.Contains(id, StringComparer.OrdinalIgnoreCase) || Current.Enabled.Contains(id);

    public static IReadOnlySet<string> ParseModules(string? raw)
    {
        var all = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return all;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Equals("all", StringComparison.OrdinalIgnoreCase))) return all;
        // Liste sırası değil, All sırası (yanıtlar kararlı olsun).
        var want = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(All.Where(want.Contains), StringComparer.OrdinalIgnoreCase);
    }

    public static string ParseTenancy(string? raw) =>
        string.Equals(raw?.Trim(), "single", StringComparison.OrdinalIgnoreCase) ? "single" : "multi";

    /// <summary>Açık modüller, All sırasıyla (API yanıtı için).</summary>
    public static string[] EnabledList() => All.Where(Current.Enabled.Contains).ToArray();

    /// <summary>Test kancası: geçerli akışta ortam yerine verilen değerleri kullanır; Dispose eskisine döner.</summary>
    public static IDisposable Override(string? modules, string? tenancy = null)
    {
        var prev = TestState.Value;
        TestState.Value = new State(ParseModules(modules), ParseTenancy(tenancy));
        return new Restore(prev);
    }

    private sealed class Restore(State? prev) : IDisposable
    {
        public void Dispose() => TestState.Value = prev;
    }
}
