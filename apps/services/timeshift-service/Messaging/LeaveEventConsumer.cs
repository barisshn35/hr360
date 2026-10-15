using System.Text.Json;
using TimeShiftService.Data;
using TimeShiftService.Models;

namespace TimeShiftService.Messaging;

/// <summary>
/// leave-service'in "leave.approved"/"leave.rejected" event'lerini dinler.
/// Onaylanan izin araligindaki HER GUN icin bir ShiftOverride (Type=Leave)
/// olusturur - kullanici elle bir sey yapmaz, izinli gunler otomatik
/// "izinli" gorunur ve vardiya hesaplamasinda pattern'in YERINE gecer.
/// </summary>
public class LeaveEventConsumer : KafkaConsumerBase
{
    public LeaveEventConsumer(IServiceProvider services, ILogger<LeaveEventConsumer> logger)
        : base(services, logger, "timeshift-service", "hr360.leave.events") { }

    protected override string ConsumerName => "timeshift-service";

    protected override async Task HandleAsync(
        string eventType, string payload, TimeShiftDbContext db, CancellationToken ct)
    {
        if (eventType == "leave.cancelled")
        {
            // Onaylı izin (başlamadan) iptal edildi: bu izin için sistemin işaretlediği günler kaldırılır.
            // Aynı çalışanın çakışan onaylı izni olamaz (leave-service reddeder); elle girilen kayıtlara dokunulmaz.
            var c = JsonSerializer.Deserialize<LeaveDecidedPayload>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is null || c.EndDate < c.StartDate || c.EndDate.DayNumber - c.StartDate.DayNumber > 366) return;
            var fullNote = $"İzin talebi {c.LeaveRequestId}";
            var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.ShiftOverrides
                .Where(o => o.EmployeeId == c.EmployeeId && o.Date >= c.StartDate && o.Date <= c.EndDate
                            && o.IsSystemManaged && o.Type == ShiftOverrideType.Leave && (o.Note == fullNote || o.IsPartial)), ct);
            db.ShiftOverrides.RemoveRange(rows);
            Logger.LogInformation("İzin iptali işlendi: çalışan {EmployeeId}, {Count} izinli gün kaldırıldı", c.EmployeeId, rows.Count);
            return;
        }
        if (eventType != "leave.approved") return; // reddedilen icin yapilacak bir sey yok

        var evt = JsonSerializer.Deserialize<LeaveDecidedPayload>(payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (evt is null || !evt.Approved) return;

        // Savunma: leave-service artik 1 yili asan talebi kabul etmiyor; yine de bozuk
        // bir olay gun gun milyonlarca sorgu calistirip tuketiciyi kilitlemesin.
        if (evt.EndDate < evt.StartDate || evt.EndDate.DayNumber - evt.StartDate.DayNumber > 366)
        {
            Logger.LogError("Geçersiz izin aralığı, atlanıyor: {Start} - {End} ({Leave})",
                evt.StartDate, evt.EndDate, evt.LeaveRequestId);
            return;
        }

        // Madde 70: yarım gün / saatlik izin (tek gün, 1 günden az) takvimde "kısmi" görünür.
        var partial = evt.StartDate == evt.EndDate && evt.Days is > 0 and < 1;
        var note = !partial ? $"İzin talebi {evt.LeaveRequestId}"
            : evt.Hours is { } h ? $"Kısmi izin: {h.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} saat"
            : "Kısmi izin: yarım gün";

        for (var date = evt.StartDate; date <= evt.EndDate; date = date.AddDays(1))
        {
            var existing = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.ShiftOverrides
                    .Where(o => o.EmployeeId == evt.EmployeeId && o.Date == date), ct);

            if (existing is not null)
            {
                existing.Type = ShiftOverrideType.Leave;
                existing.Note = note;
                existing.IsSystemManaged = true;
                existing.IsPartial = partial;
                existing.StartTime = null;
                existing.EndTime = null;
                // Bu satir mevcut olmayan (StampTenant sadece INSERT'te
                // calisir) bir kayittan geliyorsa TenantSlug zaten dogrudur;
                // yine de bos kalmis eski/bozuk bir kayitsa burada duzeltelim.
                if (string.IsNullOrEmpty(existing.TenantSlug)) existing.TenantSlug = evt.TenantSlug;
            }
            else
            {
                db.ShiftOverrides.Add(new ShiftOverride
                {
                    // Arka plan tuketicisinde (bu sinif) HTTP baglami/TenantContext
                    // yok, bu yuzden StampTenant() TenantSlug'i asla dolduramiyor
                    // (bkz. KafkaConsumerBase - tenantContext.IsPlatformAdmin=true
                    // yalnizca SORGULARI gecerli kilar, YENI kayitlara TenantSlug
                    // YAZMAZ). Elle olayin tasidigi TenantSlug'i kullaniyoruz - aksi
                    // halde bu satir TenantSlug="" ile olusur ve gercek (platform-admin
                    // olmayan) kiracı kullanicilarina HICBIR ZAMAN gorunmez (hardcore
                    // test, canli ikinci kullanici ile dogrulandi: Mehmet Demir'in
                    // rostersinde onaylanmis izin gorunmuyordu).
                    TenantSlug = evt.TenantSlug,
                    EmployeeId = evt.EmployeeId,
                    Date = date,
                    Type = ShiftOverrideType.Leave,
                    Note = note,
                    IsSystemManaged = true,
                    IsPartial = partial,
                });
            }
        }

        Logger.LogInformation(
            "İzin onayı işlendi: çalışan {EmployeeId}, {Start} - {End} arası izinli işaretlendi",
            evt.EmployeeId, evt.StartDate, evt.EndDate);
    }

    private record LeaveDecidedPayload(
        string TenantSlug, Guid LeaveRequestId, Guid EmployeeId,
        DateOnly StartDate, DateOnly EndDate, bool Approved, DateTimeOffset OccurredAt,
        string? Type = null, decimal? Days = null, decimal? Hours = null);
}
