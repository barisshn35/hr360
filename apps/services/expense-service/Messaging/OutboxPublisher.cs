using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;

namespace ExpenseService.Messaging;

/// <summary>
/// Outbox tablosunu periyodik tarar, yayinlanmamis event'leri Kafka'ya gonderir.
/// At-least-once teslim: Kafka'ya gidip DB isaretlenmeden once cokerse event
/// tekrar gonderilir - bu yuzden tuketiciler idempotent olmak zorunda.
/// </summary>
public class OutboxPublisher : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OutboxPublisher> _logger;
    private readonly string _bootstrapServers;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;
    private const int MaxAttempts = 10;

    public OutboxPublisher(IServiceProvider services, ILogger<OutboxPublisher> logger)
    {
        _services = services;
        _logger = logger;
        _bootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")
            ?? "kafka:9092";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = _bootstrapServers,
            // Kayipsizlik icin: tum replikalar onaylasin, gerekirse tekrar dene.
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 3,
        };

        using var producer = new ProducerBuilder<string, string>(config).Build();
        _logger.LogInformation("OutboxPublisher basladi. Kafka: {Servers}", _bootstrapServers);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishBatchAsync(producer, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox yayin dongusunde beklenmeyen hata");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        producer.Flush(TimeSpan.FromSeconds(5));
    }

    private async Task PublishBatchAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpenseDbContext>();

        // NOT: messaging_outbox dort servisin (employee, leave, expense, workflow)
        // ORTAK tablosu ve her birinin yayincisi ayni satirlari kilitsiz okuyordu -
        // her olay Kafka'ya birden fazla kez gidiyordu (tuketiciler tekrari ayikliyordu
        // ama bu sans eseriydi). Satirlar artik bir islem icinde FOR UPDATE SKIP LOCKED
        // ile alinir: bir satiri ayni anda yalnizca bir yayinci (ve ayni servisin tek
        // bir kopyasi) isler; yayin durumu ayni islemde yazilir.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.OutboxMessages
            .FromSqlRaw(
                "SELECT * FROM messaging_outbox " +
                "WHERE \"PublishedAt\" IS NULL AND \"AttemptCount\" < {0} " +
                "ORDER BY \"CreatedAt\" LIMIT {1} FOR UPDATE SKIP LOCKED",
                MaxAttempts, BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }

        foreach (var message in pending)
        {
            try
            {
                var kafkaMessage = new Message<string, string>
                {
                    Key = message.PartitionKey,
                    Value = message.Payload,
                    Headers = new Headers
                    {
                        { "event-type", System.Text.Encoding.UTF8.GetBytes(message.EventType) },
                        { "event-id", System.Text.Encoding.UTF8.GetBytes(message.Id.ToString()) },
                    }
                };

                // G25: W3C traceparent basligi (izleme aciksa); tuketici span'i buna baglanir.
                using var activity = ExpenseService.Observability.Telemetry.StartProducer(message.Topic, message.EventType,
                    (k, v) => kafkaMessage.Headers.Add(k, System.Text.Encoding.UTF8.GetBytes(v)));
                await producer.ProduceAsync(message.Topic, kafkaMessage, ct);

                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
                _logger.LogInformation(
                    "Event yayinlandi: {EventType} -> {Topic} (id={Id})",
                    message.EventType, message.Topic, message.Id);
            }
            catch (Exception ex)
            {
                message.AttemptCount++;
                message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                _logger.LogWarning(ex,
                    "Event yayinlanamadi: {EventType} (deneme {Attempt}/{Max})",
                    message.EventType, message.AttemptCount, MaxAttempts);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
