namespace WorkflowService.Messaging;

/// <summary>
/// Transactional outbox kaydi. Is verisiyle AYNI transaction icinde yazilir,
/// boylece "DB'ye yazildi ama event kayboldu" durumu olusmaz.
/// Arka plandaki OutboxPublisher bunlari Kafka'ya tasir.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Topic { get; set; }
    public required string EventType { get; set; }
    /// <summary>Kafka partition key - ayni varliga ait event'ler sirali kalsin diye.</summary>
    public required string PartitionKey { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
