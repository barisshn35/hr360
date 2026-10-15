using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using LeaveService.Data;
using LeaveService.Tenancy;

namespace LeaveService.Messaging;

/// <summary>
/// Kafka tuketicisi icin ortak iskelet: abone olur, event-id ile idempotency
/// kontrolu yapar, is mantigini calistirir, offset'i ELLE commit eder
/// (islem basarisiz olursa offset ilerlemesin diye).
/// </summary>
public abstract class KafkaConsumerBase : BackgroundService
{
    protected readonly IServiceProvider Services;
    protected readonly ILogger Logger;
    private readonly string _bootstrapServers;
    private readonly string _groupId;
    private readonly string[] _topics;

    protected KafkaConsumerBase(
        IServiceProvider services, ILogger logger, string groupId, params string[] topics)
    {
        Services = services;
        Logger = logger;
        _groupId = groupId;
        _topics = topics;
        _bootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")
            ?? "kafka:9092";
    }

    /// <summary>Bu tuketicinin adi - idempotency kaydinda kullanilir.</summary>
    protected abstract string ConsumerName { get; }

    /// <summary>Gercek is mantigi. Hata firlatilirsa offset commit EDILMEZ.</summary>
    protected abstract Task HandleAsync(
        string eventType, string payload, LeaveDbContext db, CancellationToken ct);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    private async Task ConsumeLoop(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = _groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Sonradan olusan konular 10 sn icinde fark edilir (varsayilan 5 dk idi:
            // yeni kurulumda ilk olaylar dakikalarca gecikiyordu).
            TopicMetadataRefreshIntervalMs = 10_000,
            EnableAutoCommit = false,   // basarili islemden sonra elle commit
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => Logger.LogWarning("Kafka hatasi: {Reason}", e.Reason))
            .Build();

        consumer.Subscribe(_topics);
        Logger.LogInformation(
            "{Consumer} basladi. Konular: {Topics}, grup: {Group}",
            ConsumerName, string.Join(", ", _topics), _groupId);

        // Ayni mesaj icin art arda basarisiz deneme takibi (bkz. catch blogu).
        TopicPartitionOffset? failingOffset = null;
        var failingAttempts = 0;
        const int MaxAttemptsPerEvent = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message is null) continue;

                var eventType = GetHeader(result.Message.Headers, "event-type");
                var eventIdRaw = GetHeader(result.Message.Headers, "event-id");
                // G25: yayincinin traceparent'i ile tuketim span'i (izleme kapaliyken null).
                using var activity = LeaveService.Observability.Telemetry.StartConsumer(
                    result.Topic, eventType, k => GetHeader(result.Message.Headers, k));

                if (!Guid.TryParse(eventIdRaw, out var eventId))
                {
                    Logger.LogWarning("event-id header'i okunamadi, mesaj atlaniyor");
                    consumer.Commit(result);
                    continue;
                }

                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LeaveDbContext>();

                // Bu bir arka plan iscisi - HTTP istegi olmadigi icin
                // TenantContext hicbir zaman doldurulmuyor. Doldurulmadan
                // ApplyTenantFilters TUM sorgulari (LeaveRequest gibi
                // ITenantOwned entity'ler icin) sessizce bos dondurur.
                var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
                tenantContext.IsPlatformAdmin = true;

                // Idempotency: bu event daha once islendi mi?
                var already = await db.ProcessedEvents
                    .AnyAsync(p => p.EventId == eventId && p.Consumer == ConsumerName, stoppingToken);
                if (already)
                {
                    Logger.LogDebug("Event zaten islenmis, atlaniyor: {EventId}", eventId);
                    consumer.Commit(result);
                    continue;
                }

                await HandleAsync(eventType ?? "", result.Message.Value, db, stoppingToken);

                db.ProcessedEvents.Add(new ProcessedEvent
                {
                    EventId = eventId,
                    EventType = eventType ?? "",
                    Consumer = ConsumerName,
                });
                await db.SaveChangesAsync(stoppingToken);

                consumer.Commit(result);
                failingOffset = null;
                failingAttempts = 0;
                Logger.LogInformation("Event islendi: {EventType} ({EventId})", eventType, eventId);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // NOT: Onceki halinde burada yalnizca log + 2 sn bekleme vardi ve
                // yorum "offset commit edilmedi: mesaj tekrar denenecek" diyordu -
                // bu YANLISTI. Confluent.Kafka'da tuketicinin bellek ici konumu
                // Consume() ile zaten ilerler; bir sonraki Consume() SONRAKI mesaji
                // dondurur. Basarisiz mesaj ancak yeniden baslatmada tekrar gelirdi,
                // sonraki herhangi bir mesaj commit edildiginde ise KALICI olarak
                // kayboluyordu (hardcore test, son tur: timeshift'te cakisan bir
                // izin onayi boyle sessizce dustu). Artik ayni offset'e Seek edip
                // artan beklemeyle yeniden deniyoruz; MaxAttemptsPerEvent'ten sonra
                // (zehirli mesaj bolumu sonsuza dek kilitlemesin diye) Critical
                // log ile atlanir.
                if (result?.Message is null)
                {
                    Logger.LogError(ex, "Kafka tuketim hatasi");
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    continue;
                }

                if (result.TopicPartitionOffset.Equals(failingOffset)) failingAttempts++;
                else { failingOffset = result.TopicPartitionOffset; failingAttempts = 1; }

                if (failingAttempts >= MaxAttemptsPerEvent)
                {
                    Logger.LogCritical(ex,
                        "Event {Attempts} denemede de islenemedi, ATLANIYOR: {Offset} ({EventType})",
                        failingAttempts, result.TopicPartitionOffset,
                        GetHeader(result.Message.Headers, "event-type"));
                    try { consumer.Commit(result); } catch (Exception commitEx) { Logger.LogError(commitEx, "Atlanan event commit edilemedi"); }
                    failingOffset = null;
                    failingAttempts = 0;
                    continue;
                }

                Logger.LogError(ex,
                    "Event islenirken hata (deneme {Attempt}/{Max}), ayni mesaj yeniden denenecek: {Offset}",
                    failingAttempts, MaxAttemptsPerEvent, result.TopicPartitionOffset);
                try { consumer.Seek(result.TopicPartitionOffset); }
                catch (Exception seekEx) { Logger.LogError(seekEx, "Seek basarisiz - mesaj yeniden baslatmada tekrar gelecek"); }
                await Task.Delay(TimeSpan.FromSeconds(2 * failingAttempts), CancellationToken.None);
            }
        }

        consumer.Close();
    }

    private static string? GetHeader(Headers? headers, string key)
    {
        if (headers is null) return null;
        return headers.TryGetLastBytes(key, out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;
    }
}
