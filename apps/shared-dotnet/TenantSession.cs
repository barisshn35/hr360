using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HR360.Platform;

/// <summary>
/// İstekteki kiracıyı PostgreSQL oturum değişkeni <c>app.tenant</c>'a taşır; satır düzeyi güvenlik
/// (RLS, scripts/sql/2026-10-20_rls.sql) bununla süzer. EF sorgu filtresinin YEDEĞİDİR: filtre unutulsa,
/// IgnoreQueryFilters ya da ham SQL'de "TenantSlug" koşulu eksik kalsa bile başka kiracının satırı gelmez.
///
/// Değer yalnızca kimliği doğrulanmış kiracı kullanıcısının isteğinde atanır (TenantMiddleware). Platform
/// yöneticisi, iç servis çağrıları, kimliksiz uçlar ve arka plan işlerinde boş kalır; politika boş değerde
/// her satırı gösterir (bu bağlamlar kendi kiracı koşullarını zaten yazar).
///
/// Npgsql havuza dönen bağlantıyı sıfırlar (DISCARD ALL), değer bir istekten ötekine sızmaz; bu yüzden
/// kiracısız bağlantıda ek sorgu gönderilmez.
/// </summary>
public static class TenantSession
{
    private static readonly AsyncLocal<string?> Slot = new();

    /// <summary>Geçerli akıştaki kiracı (yoksa null).</summary>
    public static string? Current
    {
        get => Slot.Value;
        set => Slot.Value = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Bilerek kiracılar arası bakan kısa bölüm (ör. e-postanın tüm kiracılarda benzersizliği). Yalnızca
    /// bundan SONRA açılan bağlantıları etkiler; açık bir işlemin içinde kullanmayın.
    /// <code>using (TenantSession.AllTenants()) { ... }</code>
    /// </summary>
    public static IDisposable AllTenants()
    {
        var prev = Slot.Value;
        Slot.Value = null;
        return new Restore(prev);
    }

    /// <summary>Ham bağlantı (NpgsqlDataSource) açıldıktan hemen sonra çağrılır.</summary>
    public static async Task ApplyAsync(DbConnection conn, CancellationToken ct = default)
    {
        if (Current is not { } t) return;
        await using var cmd = Command(conn, t);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static void Apply(DbConnection conn)
    {
        if (Current is not { } t) return;
        using var cmd = Command(conn, t);
        cmd.ExecuteNonQuery();
    }

    private static DbCommand Command(DbConnection conn, string tenant)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant', @t, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "t";
        p.Value = tenant;
        cmd.Parameters.Add(p);
        return cmd;
    }

    private sealed class Restore(string? prev) : IDisposable
    {
        public void Dispose() => Slot.Value = prev;
    }
}

/// <summary>EF her bağlantı açışında kiracıyı oturuma yazar: <c>.AddInterceptors(TenantSessionInterceptor.Instance)</c>.</summary>
public sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    public static readonly TenantSessionInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        TenantSession.Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
        TenantSession.ApplyAsync(connection, cancellationToken);
}
