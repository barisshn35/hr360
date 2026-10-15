using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NotificationService.Data;
using NotificationService.Preferences;
using NotificationService.Tenancy;

namespace NotificationService.Controllers;

/// <summary>
/// Dalga 12: oturumdaki kullanıcının arayüz tercihleri (anahtar → JSON).
///   views:&lt;tablo&gt;  kayıtlı liste görünümleri (madde 87)
///   dashboard        ana panel düzeni (madde 89)
///   whatsnew         "Yenilikler" panelinde son görülen not (madde 90)
/// Yalnızca kişinin kendisi okur/yazar; başka kullanıcının tercihine uç yoktur.
/// Kiracı seçilmemişse (ör. kiracı seçmemiş platform yöneticisi) liste 204, yazım 409 döner; arayüz tarayıcıda tutar.
/// </summary>
[ApiController]
[Route("api/notifications/ui-prefs")]
[Authorize]
public class UiPrefsController : ControllerBase
{
    private readonly NotificationDbContext _db;
    private readonly ITenantContext _tenant;
    public UiPrefsController(NotificationDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    string? Sub => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    IActionResult? Guard(out string tenant, out string sub)
    {
        tenant = _tenant.TenantSlug ?? "";
        sub = Sub ?? "";
        if (string.IsNullOrWhiteSpace(tenant))
            return Conflict(new { message = "Tercihler için önce bir şirket seçin.", code = "no_tenant" });
        if (string.IsNullOrWhiteSpace(sub) || sub.Length > 64)
            return BadRequest(new { message = "Kullanıcı kimliği okunamadı." });
        return null;
    }

    /// <summary>Tüm tercihlerim: { anahtar: değer }. Kiracı seçilmemişse 204 (arayüz tarayıcıda tutar; konsolda hata olmasın).</summary>
    [HttpGet]
    public async Task<IActionResult> All(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_tenant.TenantSlug)) return NoContent();
        if (Guard(out var tenant, out var sub) is { } bad) return bad;
        var rows = await _db.UiPreferences.AsNoTracking()
            .Where(p => p.TenantSlug == tenant && p.UserSub == sub)
            .Select(p => new { p.Key, p.Value })
            .ToListAsync(ct);
        return Ok(rows.ToDictionary(r => r.Key, r => JsonSerializer.Deserialize<JsonElement>(r.Value)));
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        if (!UiPrefRules.IsValidKey(key)) return BadRequest(new { message = "Geçersiz tercih anahtarı." });
        if (Guard(out var tenant, out var sub) is { } bad) return bad;
        var row = await _db.UiPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantSlug == tenant && p.UserSub == sub && p.Key == key, ct);
        // Kayıt yoksa 204: arayüz varsayılanı kullanır (konsolda 404 gürültüsü olmasın).
        if (row is null) return NoContent();
        return Ok(new { key, value = JsonSerializer.Deserialize<JsonElement>(row.Value), updatedAt = row.UpdatedAt });
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] JsonElement value, CancellationToken ct)
    {
        if (!UiPrefRules.IsValidKey(key)) return BadRequest(new { message = "Geçersiz tercih anahtarı." });
        if (Guard(out var tenant, out var sub) is { } bad) return bad;
        var (json, error) = UiPrefRules.Normalize(value);
        if (error is not null) return BadRequest(new { message = error });

        var exists = await _db.UiPreferences.AnyAsync(p => p.TenantSlug == tenant && p.UserSub == sub && p.Key == key, ct);
        if (!exists && await _db.UiPreferences.CountAsync(p => p.TenantSlug == tenant && p.UserSub == sub, ct) >= UiPrefRules.MaxKeysPerUser)
            return BadRequest(new { message = "Tercih sayısı sınırına ulaşıldı." });

        // Ham SQL upsert: denetim kaydı (audit_log) tercih değişiklikleriyle dolmasın; eşzamanlı iki
        // sekme aynı anahtarı yazarsa da tekil indeks çakışması yerine son yazım kazanır.
        await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO notification_ui_prefs ("Id", "TenantSlug", "UserSub", "Key", "Value", "UpdatedAt")
            VALUES (@id, @tenant, @sub, @key, @value, now())
            ON CONFLICT ("TenantSlug", "UserSub", "Key") DO UPDATE SET "Value" = EXCLUDED."Value", "UpdatedAt" = now()
            """,
            new object[]
            {
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
                new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenant },
                new NpgsqlParameter("sub", NpgsqlDbType.Varchar) { Value = sub },
                new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = key },
                new NpgsqlParameter("value", NpgsqlDbType.Jsonb) { Value = json! },
            }, ct);
        return Ok(new { key, value });
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        if (!UiPrefRules.IsValidKey(key)) return BadRequest(new { message = "Geçersiz tercih anahtarı." });
        if (Guard(out var tenant, out var sub) is { } bad) return bad;
        await _db.Database.ExecuteSqlRawAsync(
            """DELETE FROM notification_ui_prefs WHERE "TenantSlug" = @tenant AND "UserSub" = @sub AND "Key" = @key""",
            new object[]
            {
                new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenant },
                new NpgsqlParameter("sub", NpgsqlDbType.Varchar) { Value = sub },
                new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = key },
            }, ct);
        return NoContent();
    }
}
