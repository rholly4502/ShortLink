using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ShortLink.Core;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Messaging;

namespace ShortLink.Worker;

/// <summary>
/// BackgroundService yang mengonsumsi pesan klik dari RabbitMQ.
/// Mempersist setiap event klik ke tabel `clicks` via ClickIngestionService (P4.1).
/// </summary>
public sealed class ClickEventConsumer : BackgroundService
{
    private readonly ILogger<ClickEventConsumer> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _connectionString;

    public ClickEventConsumer(ILogger<ClickEventConsumer> logger, IServiceScopeFactory scopeFactory, IConfiguration config)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _connectionString = config["RabbitMQ:ConnectionString"]
            ?? throw new InvalidOperationException("RabbitMQ:ConnectionString tidak ditemukan");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };
        var connection = await factory.CreateConnectionAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(RabbitMqClickEventPublisher.Exchange, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(RabbitMqClickEventPublisher.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(RabbitMqClickEventPublisher.Queue, RabbitMqClickEventPublisher.Exchange, "#", cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var clickEvent = JsonSerializer.Deserialize<ClickEvent>(ea.Body.Span);
                if (clickEvent is null)
                {
                    _logger.LogWarning("Event klik null/tidak valid; NACK tanpa requeue");
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                    return;
                }

                await using (var scope = _scopeFactory.CreateAsyncScope())
                {
                    var ingestion = scope.ServiceProvider.GetRequiredService<ClickIngestionService>();
                    var click = await ingestion.PersistAsync(clickEvent, stoppingToken);

                    var aggregation = scope.ServiceProvider.GetRequiredService<ClickAggregationService>();
                    await aggregation.AggregateAsync(new[] { click }, stoppingToken);
                }

                _logger.LogInformation("Click event dipersist: {ShortenedUrlId} pada {OccurredAt} (device {Device})",
                    clickEvent.ShortenedUrlId, clickEvent.OccurredAt, clickEvent.Device);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gagal memproses event klik; NACK tanpa requeue");
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(RabbitMqClickEventPublisher.Queue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        _logger.LogInformation("ClickEventConsumer mendengarkan queue '{Queue}'", RabbitMqClickEventPublisher.Queue);

        await WaitForCancellation(stoppingToken);

        await channel.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static Task WaitForCancellation(CancellationToken token)
    {
        var tcs = new TaskCompletionSource();
        token.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
