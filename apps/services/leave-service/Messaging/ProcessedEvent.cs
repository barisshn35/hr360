namespace LeaveService.Messaging;

/// <summary>
/// Islenmis event kimlikleri. At-least-once teslimde ayni event birden fazla
/// gelebilir; bu tablo sayesinde is mantigi yalnizca bir kez calisir.
/// </summary>
public class ProcessedEvent
{
    public Guid EventId { get; set; }
    public required string EventType { get; set; }
    public required string Consumer { get; set; }
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}
