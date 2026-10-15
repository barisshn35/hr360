using ExpenseService.Security;
using Xunit;

namespace Expense.Tests;

/// <summary>Sırların dosyadan okunması (Security/SecretEnv.cs, her serviste aynı kopya).</summary>
public class SecretEnvTests
{
    private static Func<string, string> Files(Dictionary<string, string> files) =>
        path => files.TryGetValue(path, out var v) ? v : throw new FileNotFoundException(path);

    [Fact]
    public void Dosya_yalnizca_degisken_bossa_okunur()
    {
        var env = new Dictionary<string, string?>
        {
            ["TENANT_SECRET_KEY_FILE"] = "/run/secrets/tenant_secret_key",
            ["INTERNAL_SERVICE_TOKEN"] = "ortamdaki",
            ["INTERNAL_SERVICE_TOKEN_FILE"] = "/run/secrets/token",
            ["SSL_CERT_FILE"] = "/etc/ssl/cert.pem",   // bilinmeyen ad: dokunulmaz
        };
        var changed = SecretEnv.Apply(env, Files(new() { ["/run/secrets/tenant_secret_key"] = "anahtar\n", ["/run/secrets/token"] = "dosyadaki" }));
        Assert.Equal("anahtar", env["TENANT_SECRET_KEY"]);
        Assert.Equal("ortamdaki", env["INTERNAL_SERVICE_TOKEN"]);
        Assert.False(env.ContainsKey("SSL_CERT"));
        Assert.Equal(new[] { "TENANT_SECRET_KEY" }, changed);
    }

    [Fact]
    public void Varsayilan_ortamda_hicbir_sey_degismez()
    {
        const string cs = "Host=postgres;Port=5432;Database=hr360_operational;Username=hr360admin;Password=gizli;Maximum Pool Size=12";
        var env = new Dictionary<string, string?> { ["X_DB_CONNECTION"] = cs, ["HR360_DB_PASSWORD"] = "baska" };
        Assert.Empty(SecretEnv.Apply(env, Files(new())));
        Assert.Equal(cs, env["X_DB_CONNECTION"]);
    }

    [Fact]
    public void Bos_parola_dosyadaki_parolayla_doldurulur()
    {
        var env = new Dictionary<string, string?>
        {
            ["X_DB_CONNECTION"] = "Host=postgres;Username=hr360admin;Password=;Maximum Pool Size=12",
            ["HR360_DB_PASSWORD_FILE"] = "/s/db",
        };
        SecretEnv.Apply(env, Files(new() { ["/s/db"] = "p;a\"s" }));
        var b = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = env["X_DB_CONNECTION"] };
        Assert.Equal("p;a\"s", b["Password"]);
        Assert.Equal("hr360admin", b["Username"]);
    }

    [Fact]
    public void Servis_parolasi_dizedekinin_yerine_gecer()
    {
        var env = new Dictionary<string, string?>
        {
            ["X_DB_CONNECTION"] = "Host=postgres;Username=hr360_x;Password=paylasilan",
            ["HR360_SERVICE_DB_PASSWORD_FILE"] = "/s/x",
        };
        SecretEnv.Apply(env, Files(new() { ["/s/x"] = "servise-ozel" }));
        var b = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = env["X_DB_CONNECTION"] };
        Assert.Equal("servise-ozel", b["Password"]);
    }

    [Fact]
    public void Baglantiya_ozel_parola_ve_saklama_baglantisi()
    {
        var env = new Dictionary<string, string?>
        {
            ["X_DB_CONNECTION"] = "Host=postgres;Username=hr360_x;Password=",
            ["RETENTION_DB_CONNECTION"] = "Host=postgres;Username=hr360_retention;Password=",
            ["HR360_SERVICE_DB_PASSWORD"] = "servis",
            ["RETENTION_DB_PASSWORD_FILE"] = "/s/r",
        };
        SecretEnv.Apply(env, Files(new() { ["/s/r"] = "saklama" }));
        string Pw(string k) => (string)new System.Data.Common.DbConnectionStringBuilder { ConnectionString = env[k] }["Password"];
        Assert.Equal("servis", Pw("X_DB_CONNECTION"));
        Assert.Equal("saklama", Pw("RETENTION_DB_CONNECTION"));
    }

    [Fact]
    public void Okunamayan_ya_da_bos_dosya_acilisi_durdurur()
    {
        var env = new Dictionary<string, string?> { ["TENANT_SECRET_KEY_FILE"] = "/yok" };
        var ex = Assert.Throws<InvalidOperationException>(() => SecretEnv.Apply(env, Files(new())));
        Assert.Contains("TENANT_SECRET_KEY_FILE", ex.Message);
        env = new() { ["TENANT_SECRET_KEY_FILE"] = "/bos" };
        Assert.Throws<InvalidOperationException>(() => SecretEnv.Apply(env, Files(new() { ["/bos"] = "\n" })));
    }
}
