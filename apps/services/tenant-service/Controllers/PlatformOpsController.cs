using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Arayüzden yedek / geri yükleme / güncelleme (yenilik haritası #34). Yalnızca platform yöneticisi.
/// Bu servis sunucu komutu çalıştırmaz: işi platform_ops_jobs kuyruğuna yazar, sunucudaki
/// scripts/ops-runner.py yalnızca izinli komutları çalıştırır ve sonucu yazar. Geri yükleme ve geri
/// alma ile anlık geri dönüş, modül kapatma ve çok şirketli kipe geçiş yazılı onay ister; aynı anda tek yıkıcı iş sürebilir. Her istek audit_log'a yazılır.
/// </summary>
[ApiController]
[Route("api/platform/ops")]
[Authorize(Policy = "RequirePlatformAdmin")]
public partial class PlatformOpsController : ControllerBase
{
    public static readonly string[] Kinds = ["backup", "backup-verify", "schedule", "unschedule", "check-updates", "update", "rollback", "restore", "db-isolation",
        "pitr-enable", "pitr-disable", "pitr-restore", "pitr-cleanup", "modules-set", "tenancy-multi", "tenancy-single",
        "monitoring-enable", "monitoring-disable", "monitoring-alerts", "monitoring-test", "monitoring-tracing"];
    // Servisleri yeniden başlatan / derleyen işler de sürmekte olan iş varken kuyruğa alınmaz.
    private static readonly string[] Destructive = ["update", "rollback", "restore", "pitr-restore", "modules-set", "tenancy-multi", "tenancy-single",
        "monitoring-enable", "monitoring-disable", "monitoring-tracing"];
    public const string RestoreConfirm = "GERİ YÜKLE", RollbackConfirm = "GERİ AL", PitrConfirm = "GERİ DÖN",
        ModuleOffConfirm = "MODÜLÜ KAPAT", MultiTenancyConfirm = "ÇOK ŞİRKET";
    /// <summary>İsteğe bağlı modüller (scripts/lib/modules.sh tablo sırası; çalıştırıcıdaki MODULES ile aynı).</summary>
    public static readonly string[] Modules = ["recruitment", "onboarding", "timeshift", "performance", "learning", "compensation", "ml", "expense"];
    /// <summary>Çalıştırıcı son tur bundan eskiyse "çalışmıyor" sayılır.</summary>
    private static readonly TimeSpan RunnerStale = TimeSpan.FromMinutes(3);

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")] private static partial Regex TagRx();
    [GeneratedRegex(@"^hr360-[A-Za-z0-9_.-]+\.tar\.gz(\.enc)?$")] private static partial Regex ArchiveRx();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")] private static partial Regex AtRx();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}\z")] private static partial Regex TargetRx();
    // Çalıştırıcıdaki EMAIL / WEBHOOK ile aynı (orada da ayrıca denetlenir).
    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\z")] private static partial Regex EmailRx();
    [GeneratedRegex(@"^https://[^\s'""#$`\\]{8,500}\z")] private static partial Regex WebhookRx();
    [GeneratedRegex(@"^(https?://[^/?#]+)")] private static partial Regex OriginRx();

    /// <summary>Webhook adresi sırdır: listelerde ve denetim kaydında yalnızca kökeni gösterilir.</summary>
    public static string MaskUrl(string? url) => OriginRx().Match(url ?? "") is { Success: true } m ? m.Groups[1].Value + "/…" : "…";

    /// <summary>Slack/Teams alarm işinin bağımsız değişkenlerindeki adres maskelenir (diğer işler olduğu gibi).</summary>
    public static JsonElement RedactArgs(string kind, JsonElement args)
    {
        if (kind != "monitoring-alerts" || args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("channel", out var ch) || ch.GetString() is not ("slack" or "teams")
            || !args.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()))
            return args;
        return JsonSerializer.SerializeToElement(new { channel = ch.GetString(), value = MaskUrl(v.GetString()) });
    }

    private readonly TenantDbContext _db;
    private readonly SecurityNotifier _audit;
    public PlatformOpsController(TenantDbContext db, SecurityNotifier audit) { _db = db; _audit = audit; }

    private string? ActorName => User.FindFirst("preferred_username")?.Value ?? User.FindFirst("email")?.Value;
    private string? ActorId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    public sealed class SnapRow { public string Snapshot { get; set; } = "{}"; public DateTime UpdatedAt { get; set; } }
    public sealed class JobRow
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public string Args { get; set; } = "{}";
        public string Status { get; set; } = "";
        public string? RequestedBy { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int? ExitCode { get; set; }
        public string? Output { get; set; }
    }

    private async Task<(JsonElement? Snap, DateTime? At)> SnapshotAsync(CancellationToken ct)
    {
        var row = (await _db.Database.SqlQuery<SnapRow>($"""SELECT "Snapshot"::text AS "Snapshot", "UpdatedAt" FROM platform_ops_status WHERE "Id" = 1""").ToListAsync(ct)).FirstOrDefault();
        return row is null ? (null, null) : (JsonDocument.Parse(row.Snapshot).RootElement.Clone(), row.UpdatedAt);
    }

    private static object View(JobRow j, bool withOutput) => new
    {
        j.Id, j.Kind, args = RedactArgs(j.Kind, JsonDocument.Parse(j.Args).RootElement), j.Status, j.RequestedBy, j.RequestedAt, j.StartedAt, j.FinishedAt, j.ExitCode,
        output = withOutput ? j.Output : null,
    };

    /// <summary>Sunucu durum özeti (sürüm, etiketler, yedekler, zamanlama, disk) ve çalıştırıcı sağlığı.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var (snap, at) = await SnapshotAsync(ct);
        var active = await _db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM platform_ops_jobs WHERE "Status" IN ('Pending','Running')""").SingleAsync(ct);
        return Ok(new { runnerOk = at is { } t && DateTime.UtcNow - t < RunnerStale, updatedAt = at, snapshot = snap, activeJobs = active });
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> Jobs(CancellationToken ct) =>
        Ok((await _db.Database.SqlQuery<JobRow>($"""
            SELECT "Id","Kind","Args"::text AS "Args","Status","RequestedBy","RequestedAt","StartedAt","FinishedAt","ExitCode", NULL::text AS "Output"
            FROM platform_ops_jobs ORDER BY "RequestedAt" DESC LIMIT 50
            """).ToListAsync(ct)).Select(j => View(j, false)));

    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> Job(Guid id, CancellationToken ct)
    {
        var j = (await _db.Database.SqlQuery<JobRow>($"""
            SELECT "Id","Kind","Args"::text AS "Args","Status","RequestedBy","RequestedAt","StartedAt","FinishedAt","ExitCode","Output"
            FROM platform_ops_jobs WHERE "Id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();
        return j is null ? NotFound() : Ok(View(j, true));
    }

    public sealed record JobInput(string Kind, JsonElement? Args, string? Confirm);

    /// <summary>Türe göre bağımsız değişkenleri doğrular (yedek / etiket / an, sunucu özetine göre) ve
    /// kuyruğa yazılacak hâlini döner; uygun değilse kullanıcıya gösterilecek hata.</summary>
    public static (object? Args, string? Error) ValidateArgs(string kind, JsonElement? body, string? confirm, JsonElement snap, DateTime utcNow)
    {
        var a = body is { ValueKind: JsonValueKind.Object } o ? o : default;
        string? Str(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        int? Int(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        var backups = snap.TryGetProperty("backups", out var b) ? b.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToHashSet() : [];
        var tags = snap.TryGetProperty("tags", out var tg) ? tg.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];

        switch (kind)
        {
            case "backup-verify":
                var va = Str("archive");
                if (va is not null && !(ArchiveRx().IsMatch(va) && backups.Contains(va))) return (null, "Yedek bulunamadı");
                return (va is null ? new { } : new { archive = va }, null);
            case "schedule":
                var at = Str("at"); var keep = Int("keep");
                if (at is null || !AtRx().IsMatch(at)) return (null, "Saat SS:DD biçiminde olmalı");
                if (keep is not (>= 1 and <= 365)) return (null, "Saklanacak yedek sayısı 1-365 olmalı");
                return (new { at, keep, verifyWeekly = Bool("verifyWeekly") }, null);
            case "update":
                var tag = Str("ref");
                if (tag is null || !TagRx().IsMatch(tag) || !tags.Contains(tag)) return (null, "Sürüm etiketi bulunamadı; önce güncellemeleri denetleyin");
                return (new { @ref = tag }, null);
            case "rollback":
                if (confirm != RollbackConfirm) return (null, $"Onay için \"{RollbackConfirm}\" yazın");
                return (new { }, null);
            case "restore":
                var ra = Str("archive");
                if (ra is null || !ArchiveRx().IsMatch(ra) || !backups.Contains(ra)) return (null, "Yedek bulunamadı");
                if (confirm != RestoreConfirm) return (null, $"Onay için \"{RestoreConfirm}\" yazın");
                return (new { archive = ra, onlyDb = Bool("onlyDb") }, null);
            case "pitr-disable":
                return (new { purge = Bool("purge") }, null);
            case "pitr-restore":
                var target = Str("target");
                if (target is null || !TargetRx().IsMatch(target)
                    || !DateTime.TryParseExact(target, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                    return (null, "Geri dönülecek an YYYY-AA-GG SS:DD biçiminde olmalı");
                var targetUtc = IstanbulToUtc(local);
                if (targetUtc >= utcNow) return (null, "Geri dönülecek an geçmişte olmalı");
                var oldest = snap.TryGetProperty("pitr", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("oldestRecoverable", out var ov) && ov.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(ov.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ou) ? ou : (DateTime?)null;
                if (oldest is null) return (null, "Geri dönülebilecek an yok; önce anlık geri dönüşü açın");
                if (targetUtc <= oldest) return (null, "Bu an geri dönülebilecek aralığın dışında");
                if (confirm != PitrConfirm) return (null, $"Onay için \"{PitrConfirm}\" yazın");
                return (new { target }, null);
            case "modules-set":
                if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("modules", out var mv) || mv.ValueKind != JsonValueKind.Array)
                    return (null, "Modül seçimi liste olmalı");
                var picked = new List<string>();
                foreach (var m in mv.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.String || m.GetString() is not { } id || !Modules.Contains(id)) return (null, "Bilinmeyen modül");
                    if (picked.Contains(id)) return (null, "Modül seçimi yinelenen öğe içeriyor");
                    picked.Add(id);
                }
                var selection = Modules.Where(picked.Contains).ToArray();
                // Şu an açık olanlar sunucu özetinden; eski çalıştırıcıda özet yoksa hepsi açık sayılır (kurulum varsayılanı).
                var enabledNow = snap.TryGetProperty("modules", out var ms) && ms.ValueKind == JsonValueKind.Object
                    && ms.TryGetProperty("enabled", out var me) && me.ValueKind == JsonValueKind.Array
                    ? me.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet()
                    : Modules.ToHashSet();
                if (enabledNow.Any(e => Modules.Contains(e) && !selection.Contains(e)) && confirm != ModuleOffConfirm)
                    return (null, $"Modül kapatmak için \"{ModuleOffConfirm}\" yazın");
                return (new { modules = selection }, null);
            case "tenancy-single":
                var tenants = snap.TryGetProperty("tenancy", out var tn) && tn.ValueKind == JsonValueKind.Object
                    && tn.TryGetProperty("tenants", out var tc) && tc.ValueKind == JsonValueKind.Number && tc.TryGetInt32(out var n) ? n : (int?)null;
                if (tenants != 1) return (null, "Tek şirket kipine yalnızca tam olarak bir şirket varken geçilebilir");
                return (new { }, null);
            case "tenancy-multi":
                if (confirm != MultiTenancyConfirm) return (null, $"Onay için \"{MultiTenancyConfirm}\" yazın");
                return (new { }, null);
            case "monitoring-disable":
                return (new { purge = Bool("purge") }, null);
            case "monitoring-alerts":
                var channel = Str("channel");
                if (channel is not ("email" or "slack" or "teams")) return (null, "Bilinmeyen alarm kanalı");
                var value = Str("value")?.Trim();
                if (string.IsNullOrEmpty(value)) return (new { channel, value = (string?)null }, null);
                if (channel == "email")
                {
                    var addrs = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (addrs.Length is < 1 or > 10) return (null, "En fazla 10 e-posta adresi girilebilir");
                    if (addrs.FirstOrDefault(x => !EmailRx().IsMatch(x)) is { } badAddr) return (null, $"Geçersiz e-posta adresi: {badAddr}");
                    return (new { channel, value = string.Join(',', addrs) }, null);
                }
                if (!WebhookRx().IsMatch(value)) return (null, "Webhook adresi https:// ile başlamalı ve boşluk içermemeli");
                return (new { channel, value }, null);
            case "monitoring-test":
            case "monitoring-tracing":
                var monOn = snap.TryGetProperty("monitoring", out var mon) && mon.ValueKind == JsonValueKind.Object
                    && mon.TryGetProperty("enabled", out var monEnabled) && monEnabled.ValueKind == JsonValueKind.True;
                if (kind == "monitoring-test")
                    return monOn ? (new { }, null) : (null, "Önce izlemeyi açın");
                if (!Bool("on")) return (new { on = false }, null);
                if (!monOn) return (null, "Önce izlemeyi açın");
                var ratio = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("ratio", out var rv) && rv.ValueKind == JsonValueKind.Number
                    && rv.TryGetDecimal(out var dec) ? dec : (decimal?)null;
                if (ratio is not (>= 0.001m and <= 1m)) return (null, "Örnekleme oranı 0 ile 1 arasında olmalı");
                return (new { on = true, ratio = Math.Round(ratio.Value, 3).ToString("0.###", CultureInfo.InvariantCulture) }, null);
            default:
                return (new { }, null);
        }
    }

    /// <summary>Yerel İstanbul saatini UTC'ye çevirir (saat dilimi verisi yoksa sabit +03:00; 2016'dan beri).</summary>
    private static DateTime IstanbulToUtc(DateTime local)
    {
        try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")); }
        catch (TimeZoneNotFoundException) { return DateTime.SpecifyKind(local.AddHours(-3), DateTimeKind.Utc); }
    }

    /// <summary>İşi kuyruğa yazar. Bağımsız değişkenler burada ve çalıştırıcıda ayrı ayrı doğrulanır.</summary>
    [HttpPost("jobs")]
    public async Task<IActionResult> Enqueue([FromBody] JobInput body, CancellationToken ct)
    {
        if (!Kinds.Contains(body.Kind)) return BadRequest(new { message = "İzinli olmayan iş türü" });
        var (snap, at) = await SnapshotAsync(ct);
        if (snap is null || at is null || DateTime.UtcNow - at > RunnerStale)
            return BadRequest(new { message = "Bakım çalıştırıcısı çalışmıyor. Sunucuda bir kez scripts/ops-runner.sh install çalıştırın.", code = "runner_down" });
        var (args, error) = ValidateArgs(body.Kind, body.Args, body.Confirm, snap.Value, DateTime.UtcNow);
        if (error is not null) return BadRequest(new { message = error });
        var busy = await _db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM platform_ops_jobs WHERE "Status" IN ('Pending','Running')""").SingleAsync(ct);
        if (busy > 0 && Destructive.Contains(body.Kind))
            return Conflict(new { message = "Sürmekte olan bir bakım işi var; bitmesini bekleyin." });
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(args);
        var by = ActorName ?? ActorId ?? "platform-admin";
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform_ops_jobs ("Id","Kind","Args","Status","RequestedBy","RequestedAt") VALUES ({id}, {body.Kind}, {json}::jsonb, 'Pending', {by}, now())
            """, ct);
        await _audit.AuditAsync(null, "PlatformOps", id.ToString(), $"PlatformOps:{body.Kind}", RedactArgs(body.Kind, JsonSerializer.SerializeToElement(args)), ActorId, ActorName, ct);
        return Ok(new { id, status = "Pending" });
    }

    /// <summary>Henüz başlamamış işi iptal eder.</summary>
    [HttpPost("jobs/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var n = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform_ops_jobs SET "Status" = 'Cancelled', "FinishedAt" = now() WHERE "Id" = {id} AND "Status" = 'Pending'
            """, ct);
        if (n == 0) return BadRequest(new { message = "Yalnızca bekleyen iş iptal edilebilir" });
        await _audit.AuditAsync(null, "PlatformOps", id.ToString(), "PlatformOps:cancel", new { }, ActorId, ActorName, ct);
        return Ok(new { id, status = "Cancelled" });
    }
}
