using System.Collections.Concurrent;

namespace RecruitmentService.Services;

/// <summary>
/// Y16: herkese açık POST uçları için IP başına kayan pencereli sınır (yalnızca bellekte; IP adresi
/// veritabanına yazılmaz). RECRUITMENT_PUBLIC_RATE_LIMIT (varsayılan 10) istek /
/// RECRUITMENT_PUBLIC_RATE_WINDOW_SECONDS (varsayılan 600 sn).
/// </summary>
public class PublicRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();
    private DateTime _lastSweep = DateTime.UtcNow;
    public int Limit { get; }
    public TimeSpan Window { get; }

    public PublicRateLimiter() : this(
        int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_PUBLIC_RATE_LIMIT"), out var l) && l > 0 ? l : 10,
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("RECRUITMENT_PUBLIC_RATE_WINDOW_SECONDS"), out var w) && w > 0 ? w : 600))
    { }

    public PublicRateLimiter(int limit, TimeSpan window)
    {
        Limit = limit;
        Window = window;
    }

    /// <summary>İsteğe izin verilirse true (ve sayılır); sınır aşıldıysa false.</summary>
    public bool TryAcquire(string key, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        Sweep(now);
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
            if (q.Count >= Limit) return false;
            q.Enqueue(now);
            return true;
        }
    }

    private void Sweep(DateTime now)
    {
        if (now - _lastSweep < TimeSpan.FromMinutes(5)) return;
        _lastSweep = now;
        foreach (var (k, q) in _hits)
            lock (q)
                if (q.Count == 0 || now - q.Last() >= Window) _hits.TryRemove(k, out _);
    }

    /// <summary>Gateway'in koyduğu X-Real-IP (istemci başlığı gateway'de ezilir), yoksa bağlantı adresi.</summary>
    public static string ClientKey(HttpContext http) =>
        http.Request.Headers["X-Real-IP"].FirstOrDefault()?.Trim() is { Length: > 0 } ip ? ip
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
