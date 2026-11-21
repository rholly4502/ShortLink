using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Messaging;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Worker;

public enum MessageDisposition { Ack, NackRequeue }

public sealed record IncomingClickMessage(ulong DeliveryTag, ReadOnlyMemory<byte> Body, string Exchange, string RoutingKey);

public sealed record DeadLetterMessage(
    ReadOnlyMemory<byte> Body,
    string Reason,
    string OriginalExchange,
    string OriginalRoutingKey,
    string OriginalQueue,
    int RetryAttempts,
    string ErrorType,
    string ErrorMessage,
    DateTimeOffset FailedAt);

public interface IClickEventHandler
{
    Task HandleAsync(ClickEvent clickEvent, CancellationToken ct = default);
}

public interface IDeadLetterPublisher
{
    Task PublishAsync(DeadLetterMessage message, CancellationToken ct = default);
}

public sealed class ScopedClickEventHandler(IServiceScopeFactory scopeFactory) : IClickEventHandler
{
    public async Task HandleAsync(ClickEvent clickEvent, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<ClickIngestionService>();
        var aggregation = scope.ServiceProvider.GetRequiredService<ClickAggregationService>();
        var db = scope.ServiceProvider.GetService<AppDbContext>();

        if (db is not null && db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var click = await ingestion.PersistAsync(clickEvent, ct);
            await aggregation.AggregateAsync(new[] { click }, ct);
            await tx.CommitAsync(ct);
            return;
        }

        var persisted = await ingestion.PersistAsync(clickEvent, ct);
        await aggregation.AggregateAsync(new[] { persisted }, ct);
    }
}

/// <summary>Memproses event klik dengan validasi payload, retry/backoff eksponensial, dan dead-letter fallback.</summary>
public sealed class ClickEventMessageProcessor
{
    private const int MaxErrorMessageLength = 500;
    private readonly IClickEventHandler _handler;
    private readonly ClickConsumerOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<ClickEventMessageProcessor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    [ActivatorUtilitiesConstructor]
    public ClickEventMessageProcessor(
        IClickEventHandler handler, IOptions<ClickConsumerOptions> options, IClock clock, ILogger<ClickEventMessageProcessor> logger)
        : this(handler, options, clock, logger, ClickConsumerOptions.DelayAsync)
    {
    }

    public ClickEventMessageProcessor(
        IClickEventHandler handler,
        IOptions<ClickConsumerOptions> options,
        IClock clock,
        ILogger<ClickEventMessageProcessor> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _handler = handler;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _delayAsync = delayAsync;
    }

    public async Task<MessageDisposition> ProcessAsync(
        IncomingClickMessage message, IDeadLetterPublisher deadLetterPublisher, CancellationToken ct = default)
    {
        if (!TryDeserialize(message.Body.Span, out var clickEvent, out var errorType, out var errorMessage))
        {
            _logger.LogWarning("Invalid click payload on delivery {DeliveryTag} ({ErrorType}); routing to DLQ.",
                message.DeliveryTag, errorType);
            return await TryDeadLetterAsync(message, deadLetterPublisher, "invalid-payload", 0, errorType, errorMessage, ct);
        }

        var maxRetries = Math.Max(0, _options.MaxRetryAttempts);
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ClickConsumerOptions.ComputeBackoffDelay(attempt, _options.InitialRetryDelay, _options.MaxRetryDelay);
                _logger.LogWarning(lastException, "Retrying click event {ShortenedUrlId} (attempt {Attempt}/{MaxRetries}) after {DelayMs}ms.",
                    clickEvent!.ShortenedUrlId, attempt, maxRetries, (int)delay.TotalMilliseconds);

                try
                {
                    await _delayAsync(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Worker shutdown requested while backing off for delivery {DeliveryTag}; requeueing.",
                        message.DeliveryTag);
                    return MessageDisposition.NackRequeue;
                }
            }

            try
            {
                await _handler.HandleAsync(clickEvent!, ct);
                _logger.LogInformation("Click event dipersist: {ShortenedUrlId} pada {OccurredAt} (device {Device})",
                    clickEvent!.ShortenedUrlId, clickEvent.OccurredAt, clickEvent.Device);
                return MessageDisposition.Ack;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Worker shutdown requested while processing delivery {DeliveryTag}; requeueing.",
                    message.DeliveryTag);
                return MessageDisposition.NackRequeue;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        _logger.LogError(lastException, "Click event {ShortenedUrlId} failed after {MaxRetries} retries; routing to dead-letter queue '{DeadLetterQueue}'.",
            clickEvent!.ShortenedUrlId, maxRetries, _options.DeadLetterQueue);

        return await TryDeadLetterAsync(
            message, deadLetterPublisher, "processing-failed", maxRetries,
            lastException!.GetType().FullName ?? lastException.GetType().Name, lastException.Message, ct);
    }

    private static bool TryDeserialize(
        ReadOnlySpan<byte> body, out ClickEvent? clickEvent, out string errorType, out string errorMessage)
    {
        clickEvent = null;
        if (body.IsEmpty)
            return Fail(nameof(JsonException), "Empty click event payload.", out errorType, out errorMessage);

        try
        {
            clickEvent = JsonSerializer.Deserialize<ClickEvent>(body);
        }
        catch (JsonException ex)
        {
            return Fail(ex.GetType().FullName ?? nameof(JsonException), ex.Message, out errorType, out errorMessage);
        }

        if (clickEvent is null)
            return Fail("NullPayload", "Deserialized ClickEvent was null.", out errorType, out errorMessage);

        if (clickEvent.ShortenedUrlId == Guid.Empty)
            return Fail(nameof(ArgumentException), "ClickEvent.ShortenedUrlId cannot be empty.", out errorType, out errorMessage);

        errorType = string.Empty;
        errorMessage = string.Empty;
        return true;

        static bool Fail(string type, string msg, out string errType, out string errMsg)
        {
            errType = type;
            errMsg = msg;
            return false;
        }
    }

    private async Task<MessageDisposition> TryDeadLetterAsync(
        IncomingClickMessage message, IDeadLetterPublisher deadLetterPublisher,
        string reason, int retryAttempts, string errorType, string? errorMessage, CancellationToken ct)
    {
        var truncated = string.IsNullOrEmpty(errorMessage)
            ? string.Empty
            : (errorMessage.Length <= MaxErrorMessageLength ? errorMessage : errorMessage[..MaxErrorMessageLength]);

        var dlqMessage = new DeadLetterMessage(
            message.Body, reason, message.Exchange, message.RoutingKey,
            RabbitMqClickEventPublisher.Queue, retryAttempts, errorType, truncated, _clock.UtcNow);

        try
        {
            await deadLetterPublisher.PublishAsync(dlqMessage, ct);
            return MessageDisposition.Ack;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Shutdown interrupted dead-letter publish for delivery {DeliveryTag}; requeueing.", message.DeliveryTag);
            return MessageDisposition.NackRequeue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish delivery {DeliveryTag} to dead-letter queue '{DeadLetterQueue}'; requeueing.",
                message.DeliveryTag, _options.DeadLetterQueue);
            return MessageDisposition.NackRequeue;
        }
    }
}
