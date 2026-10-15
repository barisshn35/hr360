using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using EngagementService.Data;

namespace EngagementService.Infrastructure;

/// <summary>
/// KVKK m.12 (veri güvenliği): TCKN ve IBAN veritabanında AES-256-GCM ile şifreli tutulur.
/// Biçim: "enc1:" + base64(nonce + tag + şifreli metin) ya da anahtar yenilemeden sonra
/// "enc2:&lt;anahtar kimliği&gt;:" + base64 (bkz. <see cref="EngagementService.Security.KeyRing"/>; anahtarlar
/// TENANT_SECRET_KEYS / TENANT_SECRET_KEY, governance ve tenant-service ile aynı). Önekli olmayan
/// değerler eski düz metindir; okunurken olduğu gibi döner, açılışta <see cref="PiiBackfill"/> bunları şifreler.
/// </summary>
public static class PiiProtector
{
    public const string Prefix = EngagementService.Security.KeyRing.V1;

    public static bool Enabled => EngagementService.Security.KeyRing.Enabled;

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || EngagementService.Security.KeyRing.IsSealed(plain) || !Enabled) return plain;
        return EngagementService.Security.KeyRing.Seal(plain, Prefix);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !EngagementService.Security.KeyRing.IsSealed(stored)) return stored;
        if (!Enabled) throw new InvalidOperationException("TENANT_SECRET_KEY tanımlı değil; şifreli kişisel veri açılamıyor.");
        return EngagementService.Security.KeyRing.Open(stored);
    }

    public static readonly ValueConverter<string?, string?> Converter =
        new(v => Protect(v), v => Unprotect(v));
}

/// <summary>Açılışta düz metin kalmış TCKN/IBAN değerlerini şifreler (bir kez, idempotent).</summary>
public sealed class PiiBackfill(IServiceProvider sp, ILogger<PiiBackfill> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!PiiProtector.Enabled)
        {
            log.LogWarning("TENANT_SECRET_KEY yok: TCKN ve IBAN veritabanında ŞİFRESİZ saklanıyor (KVKK m.12). .env'e ekleyin.");
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var sql = sp.GetRequiredService<Sql>();
            var ids = await sql.QueryAsync("""
                SELECT "Id" FROM engagement_profiles
                WHERE ("Iban" IS NOT NULL AND "Iban" <> '' AND "Iban" NOT LIKE 'enc1:%' AND "Iban" NOT LIKE 'enc2:%')
                   OR ("NationalId" IS NOT NULL AND "NationalId" <> '' AND "NationalId" NOT LIKE 'enc1:%' AND "NationalId" NOT LIKE 'enc2:%')
                """, r => r.GetGuid(0), ct);
            if (ids.Count == 0) return;
            foreach (var id in ids)
            {
                var row = (await sql.QueryAsync("""SELECT "Iban", "NationalId" FROM engagement_profiles WHERE "Id" = $1""",
                    r => (Iban: r.IsDBNull(0) ? null : r.GetString(0), Nid: r.IsDBNull(1) ? null : r.GetString(1)), ct, id)).First();
                await sql.ExecuteAsync("""UPDATE engagement_profiles SET "Iban" = $2, "NationalId" = $3 WHERE "Id" = $1""",
                    ct, id, (object?)PiiProtector.Protect(row.Iban) ?? DBNull.Value, (object?)PiiProtector.Protect(row.Nid) ?? DBNull.Value);
            }
            log.LogInformation("{Count} profilde TCKN/IBAN şifrelendi.", ids.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "TCKN/IBAN şifreleme turu başarısız; bir sonraki açılışta tekrar denenecek.");
        }
    }
}
