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
/// alma yazılı onay ister; aynı anda tek yıkıcı iş sürebilir. Her istek audit_log'a yazılır.
/// </summary>
[ApiController]
[Route("api/platform/ops")]
[Authorize(Policy = "RequirePlatformAdmin")]
public partial class PlatformOpsController : ControllerBase
{
    public static readonly string[] Kinds = ["backup", "backup-verify", "schedule", "unschedule", "check-updates", "update", "rollback", "restore"];
    private static readonly string[] Destructive = ["update", "rollback", "restore"];
    public const string RestoreConfirm = "GERİ YÜKLE", RollbackConfirm = "GERİ AL";
    /// <summary>Çalıştırıcı son tur bundan eskiyse "çalışmıyor" sayılır.</summary>
    private static readonly TimeSpan RunnerStale = TimeSpan.FromMinutes(3);

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")] private static partial Regex TagRx();
    [GeneratedRegex(@"^hr360-[A-Za-z0-9_.-]+\.tar\.gz(\.enc)?$")] private static partial Regex ArchiveRx();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")] private static partial Regex AtRx();

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
        j.Id, j.Kind, args = JsonDocument.Parse(j.Args).RootElement, j.Status, j.RequestedBy, j.RequestedAt, j.StartedAt, j.FinishedAt, j.ExitCode,
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

    /// <summary>İşi kuyruğa yazar. Bağımsız değişkenler burada ve çalıştırıcıda ayrı ayrı doğrulanır.</summary>
    [HttpPost("jobs")]
    public async Task<IActionResult> Enqueue([FromBody] JobInput body, CancellationToken ct)
    {
        if (!Kinds.Contains(body.Kind)) return BadRequest(new { message = "İzinli olmayan iş türü" });
        var (snap, at) = await SnapshotAsync(ct);
        if (snap is null || at is null || DateTime.UtcNow - at > RunnerStale)
            return BadRequest(new { message = "Bakım çalıştırıcısı çalışmıyor. Sunucuda bir kez scripts/ops-runner.sh install çalıştırın.", code = "runner_down" });
        var a = body.Args is { ValueKind: JsonValueKind.Object } o ? o : default;
        string? Str(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        int? Int(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        var backups = snap.Value.TryGetProperty("backups", out var b) ? b.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToHashSet() : [];
        var tags = snap.Value.TryGetProperty("tags", out var tg) ? tg.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];

        object args;
        switch (body.Kind)
        {
            case "backup-verify":
                var va = Str("archive");
                if (va is not null && !(ArchiveRx().IsMatch(va) && backups.Contains(va))) return BadRequest(new { message = "Yedek bulunamadı" });
                args = va is null ? new { } : new { archive = va };
                break;
            case "schedule":
                var at2 = Str("at"); var keep = Int("keep");
                if (at2 is null || !AtRx().IsMatch(at2)) return BadRequest(new { message = "Saat SS:DD biçiminde olmalı" });
                if (keep is not (>= 1 and <= 365)) return BadRequest(new { message = "Saklanacak yedek sayısı 1-365 olmalı" });
                args = new { at = at2, keep, verifyWeekly = Bool("verifyWeekly") };
                break;
            case "update":
                var tag = Str("ref");
                if (tag is null || !TagRx().IsMatch(tag) || !tags.Contains(tag)) return BadRequest(new { message = "Sürüm etiketi bulunamadı; önce güncellemeleri denetleyin" });
                args = new { @ref = tag };
                break;
            case "rollback":
                if (body.Confirm != RollbackConfirm) return BadRequest(new { message = $"Onay için \"{RollbackConfirm}\" yazın" });
                args = new { };
                break;
            case "restore":
                var ra = Str("archive");
                if (ra is null || !ArchiveRx().IsMatch(ra) || !backups.Contains(ra)) return BadRequest(new { message = "Yedek bulunamadı" });
                if (body.Confirm != RestoreConfirm) return BadRequest(new { message = $"Onay için \"{RestoreConfirm}\" yazın" });
                args = new { archive = ra, onlyDb = Bool("onlyDb") };
                break;
            default:
                args = new { };
                break;
        }
        var busy = await _db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM platform_ops_jobs WHERE "Status" IN ('Pending','Running')""").SingleAsync(ct);
        if (busy > 0 && Destructive.Contains(body.Kind))
            return Conflict(new { message = "Sürmekte olan bir bakım işi var; bitmesini bekleyin." });
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(args);
        var by = ActorName ?? ActorId ?? "platform-admin";
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform_ops_jobs ("Id","Kind","Args","Status","RequestedBy","RequestedAt") VALUES ({id}, {body.Kind}, {json}::jsonb, 'Pending', {by}, now())
            """, ct);
        await _audit.AuditAsync(null, "PlatformOps", id.ToString(), $"PlatformOps:{body.Kind}", args, ActorId, ActorName, ct);
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
