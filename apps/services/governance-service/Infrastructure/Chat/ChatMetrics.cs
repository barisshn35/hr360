using Prometheus;

namespace GovernanceService.Infrastructure.Chat;

/// <summary>
/// BG15: bot ölçümleri (/metrics, Prometheus). Etiketlerde kullanıcı kimliği, kiracı ya da mesaj
/// metni YOKTUR — yalnızca sağlayıcı, niyet (komut anahtarı) ve sonuç.
/// </summary>
public static class ChatMetrics
{
    public static readonly Counter Commands = Metrics.CreateCounter("hr360_chat_commands_total",
        "Sohbet botu komut/eylem sayısı", new CounterConfiguration { LabelNames = new[] { "provider", "intent", "outcome" } });

    public static readonly Histogram Latency = Metrics.CreateHistogram("hr360_chat_command_duration_seconds",
        "Sohbet botu komut yanıt süresi", new HistogramConfiguration
        {
            LabelNames = new[] { "provider" },
            Buckets = new[] { 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10 },
        });

    public static readonly Counter Sent = Metrics.CreateCounter("hr360_chat_messages_sent_total",
        "Botun gönderdiği mesajlar", new CounterConfiguration { LabelNames = new[] { "provider", "kind", "outcome" } });

    public static readonly Gauge QueueDepth = Metrics.CreateGauge("hr360_chat_outbox_depth",
        "Yeniden deneme / sessiz saat kuyruğundaki mesaj sayısı");

    public static readonly Counter RateLimited = Metrics.CreateCounter("hr360_chat_rate_limited_total",
        "Hız sınırına takılan komutlar", new CounterConfiguration { LabelNames = new[] { "provider", "scope" } });

    /// <summary>Etiket değerleri sınırlı bir kümeden gelir (serbest metin etikete yazılmaz).</summary>
    public static string Intent(string? cmd) => cmd is null or "" ? "none"
        : cmd.Length > 24 || cmd.Any(c => !(char.IsLetterOrDigit(c) || c == '_')) ? "other" : cmd;

    public static string Provider(string platform) => platform switch
    {
        "Slack" or "Teams" or "Mattermost" or "RocketChat" => platform.ToLowerInvariant(),
        _ => "other",
    };
}
