using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ShortLink.Infrastructure.Messaging;

namespace ShortLink.Worker;

public sealed class RabbitMqDeadLetterPublisher(IChannel channel, ClickConsumerOptions options) : IDeadLetterPublisher
{
    public static BasicProperties BuildDeadLetterProperties(DeadLetterMessage message) => new()
    {
        DeliveryMode = DeliveryModes.Persistent,
        ContentType = "application/json",
        Headers = new Dictionary<string, object?>
        {
            ["x-dead-letter-reason"] = message.Reason,
            ["x-original-exchange"] = message.OriginalExchange,
            ["x-original-routing-key"] = message.OriginalRoutingKey,
            ["x-original-queue"] = message.OriginalQueue,
            ["x-retry-count"] = message.RetryAttempts,
            ["x-error-type"] = message.ErrorType,
            ["x-error-message"] = message.ErrorMessage,
            ["x-failed-at"] = message.FailedAt.ToString("O")
        }
    };

    public Task PublishAsync(DeadLetterMessage message, CancellationToken ct = default)
        => channel.BasicPublishAsync(
            options.DeadLetterExchange, options.DeadLetterRoutingKey, mandatory: false,
            basicProperties: BuildDeadLetterProperties(message), body: message.Body, cancellationToken: ct).AsTask();
}

/// <summary>
/// BackgroundService yang mengonsumsi pesan klik dari RabbitMQ dengan auto-reconnect,
/// retry/backoff eksponensial, dan routing pesan poison/gagal ke dead-letter queue.
/// </summary>
public sealed class ClickEventConsumer : BackgroundService
{
    private readonly ILogger<ClickEventConsumer> _logger;
    private readonly ClickEventMessageProcessor _processor;
    private readonly ClickConsumerOptions _options;
    private readonly Func<CancellationToken, Task<IConnection>> _connectAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    [ActivatorUtilitiesConstructor]
    public ClickEventConsumer(
        ILogger<ClickEventConsumer> logger, ClickEventMessageProcessor processor,
        IOptions<ClickConsumerOptions> options, IConfiguration config)
        : this(
            logger, processor, options,
            CreateDefaultConnector(config["RabbitMQ:ConnectionString"]
                ?? throw new InvalidOperationException("RabbitMQ:ConnectionString tidak ditemukan")),
            ClickConsumerOptions.DelayAsync)
    {
    }

    public ClickEventConsumer(
        ILogger<ClickEventConsumer> logger, ClickEventMessageProcessor processor,
        IOptions<ClickConsumerOptions> options,
        Func<CancellationToken, Task<IConnection>> connectAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _logger = logger;
        _processor = processor;
        _options = options.Value;
        _connectAsync = connectAsync;
        _delayAsync = delayAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            Exception? sessionError = null;
            try
            {
                await RunConsumerSessionAsync(stoppingToken, () => connectAttempt = 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                sessionError = ex;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            connectAttempt++;
            var delay = ClickConsumerOptions.ComputeBackoffDelay(
                connectAttempt, _options.InitialConnectDelay, _options.MaxConnectDelay);
            _logger.LogWarning(sessionError,
                "RabbitMQ consumer session disconnected (attempt {Attempt}); reconnecting in {DelayMs}ms.",
                connectAttempt, (int)delay.TotalMilliseconds);

            try
            {
                await _delayAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunConsumerSessionAsync(CancellationToken stoppingToken, Action onConnected)
    {
        await using var connection = await _connectAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            RabbitMqClickEventPublisher.Exchange, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            RabbitMqClickEventPublisher.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(
            RabbitMqClickEventPublisher.Queue, RabbitMqClickEventPublisher.Exchange, "#", cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            _options.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            _options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(
            _options.DeadLetterQueue, _options.DeadLetterExchange, _options.DeadLetterRoutingKey, cancellationToken: stoppingToken);

        if (_options.PrefetchCount > 0)
            await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken: stoppingToken);

        var sessionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnShutdownAsync(object sender, ShutdownEventArgs args)
        {
            sessionEnded.TrySetResult();
            return Task.CompletedTask;
        }

        connection.ConnectionShutdownAsync += OnShutdownAsync;
        channel.ChannelShutdownAsync += OnShutdownAsync;
        await using var stopReg = stoppingToken.Register(() => sessionEnded.TrySetResult());

        try
        {
            var dlqPublisher = new RabbitMqDeadLetterPublisher(channel, _options);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                var incoming = new IncomingClickMessage(ea.DeliveryTag, ea.Body.ToArray(), ea.Exchange, ea.RoutingKey);
                var disposition = await _processor.ProcessAsync(incoming, dlqPublisher, stoppingToken);

                if (!channel.IsOpen)
                    return;

                try
                {
                    if (disposition == MessageDisposition.Ack)
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    else
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: CancellationToken.None);
                }
                catch (Exception ex) when (ex is OperationCanceledException or AlreadyClosedException or IOException)
                {
                    _logger.LogDebug(ex, "Skipped delivery settlement for tag {DeliveryTag} because channel is closing.", ea.DeliveryTag);
                }
            };

            await channel.BasicConsumeAsync(
                RabbitMqClickEventPublisher.Queue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            onConnected();
            _logger.LogInformation(
                "ClickEventConsumer mendengarkan queue '{Queue}' (prefetch={Prefetch}, dlq='{Dlq}')",
                RabbitMqClickEventPublisher.Queue, _options.PrefetchCount, _options.DeadLetterQueue);

            await sessionEnded.Task;
        }
        finally
        {
            connection.ConnectionShutdownAsync -= OnShutdownAsync;
            channel.ChannelShutdownAsync -= OnShutdownAsync;
        }
    }

    private static Func<CancellationToken, Task<IConnection>> CreateDefaultConnector(string connectionString)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString), AutomaticRecoveryEnabled = false };
        return ct => factory.CreateConnectionAsync(ct);
    }
}
