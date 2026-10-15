using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GovernanceService.Infrastructure;

namespace GovernanceService.Controllers;

/* ======================================================================
 * G2: kişinin dil tercihi (sistem bildirimleri, zamanlanmış rapor
 * bildirimi, sohbet botu yanıtları). Ayrı tablo AÇILMAZ: tercih zaten
 * notification_preferences."Language" satırında (web Profil › EN/TR ile
 * notification-service üzerinden yazılıyor; governance Notifier ve
 * ChatService aynı satırı okuyor). Bu uç yalnızca governance istemcileri
 * (ör. sohbet botu, açık API dışı otomasyon) için aynı satırı okur/yazar.
 * Çalışan kaydı olmayan kullanıcıda (platform/kiracı yöneticisi) bildirim
 * alıcısı olmadığından tercih saklanmaz; istek dili (X-HR360-Lang) geçerlidir.
 * ==================================================================== */
[Route("api/me/language")]
[Authorize]
public class LanguagePreferenceController : AppController
{
    public static readonly string[] Supported = { "tr", "en" };

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = await MyPersonAsync(ct);
        if (me is null) return Ok(new { language = Lang, stored = false, source = "request" });
        var stored = await Db.ScalarAsync("SELECT \"Language\" FROM notification_preferences WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, Tenant, me.Id) as string;
        return Ok(new { language = stored ?? "tr", stored = stored is not null, source = stored is null ? "default" : "preference" });
    }

    public record LanguageInput(string Language);

    [HttpPut]
    public async Task<IActionResult> Put(LanguageInput body, CancellationToken ct)
    {
        var lang = (body.Language ?? "").Trim().ToLowerInvariant();
        if (!Supported.Contains(lang)) return BadRequest(new { message = L("Dil 'tr' ya da 'en' olmalı.", "Language must be 'tr' or 'en'.") });
        var me = await MyPersonAsync(ct);
        if (me is null)
            return Ok(new { language = lang, stored = false, message = L("Çalışan kaydınız olmadığı için tercih saklanmadı; arayüz dili kullanılır.", "No employee record, so the preference is not stored; the interface language is used.") });
        await Db.ExecuteAsync("""
            INSERT INTO notification_preferences ("TenantSlug","EmployeeId","Language","UpdatedAt") VALUES ($1,$2,$3,now())
            ON CONFLICT ("TenantSlug","EmployeeId") DO UPDATE SET "Language" = EXCLUDED."Language", "UpdatedAt" = now()
            """, ct, Tenant, me.Id, lang);
        return Ok(new { language = lang, stored = true });
    }
}
