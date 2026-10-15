using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Messaging;
using NotificationService.Models;
using NotificationService.Preferences;
using NotificationService.Services;
using NotificationService.Tenancy;

namespace NotificationService.Controllers;

/// <summary>
/// Oturumdaki kullanıcının bildirim tercihleri: dil (arayüzde dil değiştirildiğinde çağrılır) ve
/// G11 kategori x kanal tercihleri, sessiz saatler (Europe/Istanbul), günlük özet. GET/PUT /me.
/// Güvenlik açısından kritik bildirimler (NotificationCategories.SecurityCriticalPrefixes) tercihlerden bağımsızdır.
/// </summary>
[ApiController]
[Route("api/notifications/preferences")]
[Authorize]
public class PreferencesController : ControllerBase
{
    private readonly NotificationDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly ITenantContext _tenant;
    public PreferencesController(NotificationDbContext db, EmployeeDirectoryClient employees, ITenantContext tenant)
    {
        _db = db;
        _employees = employees;
        _tenant = tenant;
    }

    /// <summary>
    /// PUT /me gövdesi: alanların hepsi isteğe bağlı. Yalnızca dil gönderen eski istemciler aynen çalışır;
    /// kategoriler / sessiz saatler / özet verilirse G11 kanal tercihleri de güncellenir.
    /// </summary>
    public record MeInput(string? Language, List<CategoryInput>? Categories, QuietInput? QuietHours, DigestInput? Digest);

    /// <summary>Bildirim dili + G11 kanal tercihleri (kategori x kanal, sessiz saatler, günlük özet).</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return Ok(Shape(EffectivePrefs.Default, false, "tr", null));
        var (g, cats) = await LoadMineAsync(me.Value, ct);
        return Ok(Shape(PreferenceStore.Compose(g, cats), true, g?.Language ?? "tr", g?.Language));
    }

    [HttpPut("me")]
    public async Task<IActionResult> Set([FromBody] MeInput body, CancellationToken ct)
    {
        var channels = body.Categories is not null || body.QuietHours is not null || body.Digest is not null;
        if (body.Language is null && !channels) return BadRequest(new { message = "Güncellenecek bir alan yok." });
        if (body.Language is not null && body.Language is not ("tr" or "en")) return BadRequest(new { message = "Dil 'tr' ya da 'en' olmalı." });
        if (channels) return await SaveAsync(body, ct);

        var me = await _employees.FindMyEmployeeIdAsync(ct);
        // Çalışan kaydı olmayan hesap (ör. platform yöneticisi) için saklanacak bir alıcı yok.
        if (me is null || string.IsNullOrWhiteSpace(_tenant.TenantSlug)) return Ok(new { language = body.Language, linked = false });
        var pref = await _db.Preferences.FirstOrDefaultAsync(p => p.EmployeeId == me, ct);
        if (pref is null)
        {
            // Anahtarın parçası olduğu için kiracı baştan yazılır (sonradan değiştirilemez).
            pref = new NotificationPreference { TenantSlug = _tenant.TenantSlug!, EmployeeId = me.Value };
            _db.Preferences.Add(pref);
        }
        pref.Language = NotificationTexts.Normalize(body.Language!);
        pref.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { language = pref.Language, linked = true });
    }

    // ---------------------------------------------------------------- G11: kanal tercihleri

    public record CategoryInput(string Key, bool InApp, bool Email, bool Push, bool Chat);
    public record QuietInput(bool Enabled, string Start, string End, int[] Days);
    public record DigestInput(bool Enabled, int Hour);
    public record ChannelsInput(List<CategoryInput>? Categories, QuietInput? QuietHours, DigestInput? Digest);

    static object Shape(EffectivePrefs eff, bool linked, string lang, string? storedLanguage)
    {
        var en = lang == "en";
        return new
        {
            linked,
            // Kayıtlı bildirim dili (yoksa null: arayüz dili kullanılır).
            language = storedLanguage,
            timeZone = "Europe/Istanbul",
            categories = NotificationCategories.All.Select(c =>
            {
                var ch = eff.For(c.Key);
                return new
                {
                    key = c.Key,
                    label = en ? c.LabelEn : c.Label,
                    mandatory = c.Mandatory,
                    critical = c.Critical,
                    digestible = c.Digestible,
                    inApp = ch.InApp, email = ch.Email, push = ch.Push, chat = ch.Chat,
                };
            }),
            quietHours = new
            {
                enabled = eff.Quiet.Enabled,
                start = eff.Quiet.Start.ToString("HH:mm"),
                end = eff.Quiet.End.ToString("HH:mm"),
                days = Enumerable.Range(0, 7).Where(d => QuietHoursCalc.DayOn(eff.Quiet.DaysMask, (DayOfWeek)d)).ToArray(),
            },
            digest = new { enabled = eff.DigestEnabled, hour = eff.DigestHour },
        };
    }

    async Task<(NotificationPreference? General, List<CategoryPreference> Cats)> LoadMineAsync(Guid me, CancellationToken ct) =>
        (await _db.Preferences.FirstOrDefaultAsync(p => p.EmployeeId == me, ct),
         await _db.CategoryPreferences.Where(c => c.EmployeeId == me).ToListAsync(ct));

    /// <summary>Eski ad (geriye uyum): GET /me ile aynı.</summary>
    [HttpGet("channels")]
    public Task<IActionResult> GetChannels(CancellationToken ct) => Get(ct);

    /// <summary>Eski ad (geriye uyum): PUT /me ile aynı (kanal tercihleri).</summary>
    [HttpPut("channels")]
    public Task<IActionResult> SetChannels([FromBody] ChannelsInput body, CancellationToken ct) =>
        SaveAsync(new MeInput(null, body.Categories, body.QuietHours, body.Digest), ct);

    async Task<IActionResult> SaveAsync(MeInput body, CancellationToken ct)
    {
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null || string.IsNullOrWhiteSpace(_tenant.TenantSlug))
            return StatusCode(403, new { message = "Hesabınıza bağlı çalışan kaydı yok" });

        TimeOnly start = default, end = default;
        if (body.QuietHours is { } q)
        {
            if (!TimeOnly.TryParseExact(q.Start ?? "", "HH:mm", out start) || !TimeOnly.TryParseExact(q.End ?? "", "HH:mm", out end))
                return BadRequest(new { message = "Sessiz saatler SS:dd biçiminde olmalı (ör. 22:00)." });
            if (q.Enabled && start == end) return BadRequest(new { message = "Sessiz saatlerin başlangıcı ve bitişi aynı olamaz." });
            if (q.Enabled && (q.Days is null || q.Days.Length == 0)) return BadRequest(new { message = "Sessiz saatler için en az bir gün seçin." });
            if (q.Days is not null && q.Days.Any(d => d is < 0 or > 6)) return BadRequest(new { message = "Geçersiz gün." });
        }
        if (body.Digest is { } dg && dg.Hour is < 0 or > 23) return BadRequest(new { message = "Özet saati 0–23 arasında olmalı." });
        if (body.Categories?.FirstOrDefault(c => !NotificationCategories.IsKnown(c.Key)) is { } unknown)
            return BadRequest(new { message = $"Bilinmeyen bildirim kategorisi: {unknown.Key}" });

        var (g, cats) = await LoadMineAsync(me.Value, ct);
        var now = DateTimeOffset.UtcNow;
        if (g is null)
        {
            g = new NotificationPreference { TenantSlug = _tenant.TenantSlug!, EmployeeId = me.Value };
            _db.Preferences.Add(g);
        }
        if (body.Language is not null) g.Language = NotificationTexts.Normalize(body.Language);
        foreach (var c in body.Categories ?? [])
        {
            // Güvenlik açısından kritik kategori kişiye göre değiştirilemez (her kanal açık).
            if (NotificationCategories.Get(c.Key).Critical) continue;
            var row = cats.FirstOrDefault(x => x.Category == c.Key);
            if (row is null)
            {
                row = new CategoryPreference { TenantSlug = _tenant.TenantSlug!, EmployeeId = me.Value, Category = c.Key };
                _db.CategoryPreferences.Add(row);
                cats.Add(row);
            }
            // Zorunlu (yasal) kategoride uygulama içi kanal kapatılamaz.
            row.InApp = c.InApp || NotificationCategories.Get(c.Key).Mandatory;
            row.Email = c.Email;
            row.Push = c.Push;
            row.Chat = c.Chat;
            row.UpdatedAt = now;
        }
        if (body.QuietHours is { } qh)
        {
            g.QuietHoursEnabled = qh.Enabled;
            g.QuietStart = start;
            g.QuietEnd = end;
            g.QuietDays = (qh.Days ?? []).Distinct().Aggregate(0, (m, d) => m | (1 << d));
        }
        var releaseDigest = false;
        if (body.Digest is { } d)
        {
            // Açılırken / saat değişirken sayaç sıfırlanır: ilk özet seçilen saatin ilk gelişinde gider.
            if (d.Enabled && (!g.DigestEnabled || g.DigestHour != d.Hour)) g.DigestLastSentAt = now;
            releaseDigest = g.DigestEnabled && !d.Enabled;
            g.DigestEnabled = d.Enabled;
            g.DigestHour = d.Hour;
        }
        g.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // Ertelenmiş gönderimler yeni tercihlerle bir sonraki turda yeniden değerlendirilir.
        var mine = _db.Notifications.Where(n => n.RecipientEmployeeId == me.Value);
        await mine.Where(n => n.DeferredUntil > now &&
                              ((n.Channel == NotificationChannel.Email && n.Status == NotificationStatus.Pending)
                               || (n.Channel == NotificationChannel.InApp && n.PushedAt == null)))
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.DeferredUntil, now), ct);
        // Özet kapatıldıysa bekleyenler tek tek gönderim kuyruğuna döner.
        if (releaseDigest)
            await mine.Where(n => n.Channel == NotificationChannel.Email && n.Status == NotificationStatus.DigestQueued)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.Status, NotificationStatus.Pending), ct);

        return Ok(Shape(PreferenceStore.Compose(g, cats), true, g.Language, g.Language));
    }

    /// <summary>
    /// Servisler arası (X-Internal-Token): bir çalışanın geçerli tercihleri. governance-service
    /// sohbet (Slack/Teams) iletiminde "chat" kanalını ve sessiz saati buradan okuyabilir.
    /// templateCode verilirse o bildirimin kategorisi ve kanal kararları da döner.
    /// </summary>
    [HttpGet("effective")]
    [AllowAnonymous]
    public async Task<IActionResult> Effective([FromQuery] Guid employeeId, [FromQuery] string? tenant, [FromQuery] string? templateCode, CancellationToken ct)
    {
        if (!InternalToken.Valid(Request)) return NotFound();
        if (employeeId == Guid.Empty) return BadRequest(new { message = "employeeId gerekli" });

        var gq = _db.Preferences.IgnoreQueryFilters().AsNoTracking().Where(p => p.EmployeeId == employeeId);
        var cq = _db.CategoryPreferences.IgnoreQueryFilters().AsNoTracking().Where(c => c.EmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            gq = gq.Where(p => p.TenantSlug == tenant);
            cq = cq.Where(c => c.TenantSlug == tenant);
        }
        var g = await gq.FirstOrDefaultAsync(ct);
        var cats = await cq.ToListAsync(ct);
        var eff = PreferenceStore.Compose(g, cats);
        var now = DateTimeOffset.UtcNow;
        var quietUntil = QuietHoursCalc.QuietUntil(now, eff.Quiet);
        object? decision = null;
        if (templateCode is not null)
        {
            var cat = NotificationCategories.Categorize(templateCode);
            var ch = eff.For(cat);
            decision = new
            {
                category = cat,
                inApp = ch.InApp,
                email = DeliveryRules.DecideEmail(templateCode, false, eff, now).Decision.ToString(),
                push = DeliveryRules.DecidePush(templateCode, eff, now).Decision.ToString(),
                // Sohbet iletimi governance'ta: kanal açık mı ve sessiz saat bitene kadar ertelenmeli mi.
                chat = ch.Chat,
                // Güvenlik açısından kritik bildirim sessiz saatte de hemen iletilir.
                chatDeferUntil = ch.Chat && !NotificationCategories.IsSecurityCritical(templateCode) ? quietUntil : null,
            };
        }
        return Ok(new
        {
            employeeId,
            stored = g is not null || cats.Count > 0,
            language = g?.Language ?? "tr",
            quietNow = quietUntil is not null,
            quietUntil,
            securityCritical = templateCode is not null && NotificationCategories.IsSecurityCritical(templateCode),
            preferences = Shape(eff, true, "tr", g?.Language),
            decision,
        });
    }
}
