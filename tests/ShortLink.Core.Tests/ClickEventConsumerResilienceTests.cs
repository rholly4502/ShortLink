using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Messaging;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;
using ShortLink.Worker;

namespace ShortLink.Core.Tests;

public sealed class ClickEventConsumerResilienceTests
{
    private readonly FixedClock _clock = new();

    [Theory]
    [InlineData(1, 500, 5000, 500)]
    [InlineData(2, 500, 5000, 1000)]
    [InlineData(3, 500, 5000, 2000)]
    [InlineData(4, 500, 5000, 4000)]
    [InlineData(5, 500, 5000, 5000)]
    [InlineData(100, 500, 5000, 5000)]
    public void ComputeBackoffDelay_ExponentialProgression_AndCapsAtMax(
        int attempt, int initialMs, int maxMs, int expectedMs)
    {
        var delay = ClickConsumerOptions.ComputeBackoffDelay(
            attempt, TimeSpan.FromMilliseconds(initialMs), TimeSpan.FromMilliseconds(maxMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    [Fact]
    public void ComputeBackoffDelay_EdgeCases_HandledSafely()
    {
        Assert.Equal(TimeSpan.Zero, ClickConsumerOptions.ComputeBackoffDelay(1, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.Zero, ClickConsumerOptions.ComputeBackoffDelay(2, TimeSpan.FromMilliseconds(-100), TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMilliseconds(200), ClickConsumerOptions.ComputeBackoffDelay(1, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200)));
        Assert.Equal(TimeSpan.FromSeconds(4), ClickConsumerOptions.ComputeBackoffDelay(3, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Equal(TimeSpan.MaxValue, ClickConsumerOptions.ComputeBackoffDelay(50, TimeSpan.FromDays(1), TimeSpan.Zero));
    }

    [Fact]
    public async Task ProcessAsync_ValidPayload_SucceedsFirstAttempt_ReturnsAckWithoutRetryOrDlq()
    {
        var handler = new FakeClickEventHandler();
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = CreateProcessor(handler, delays);
        var evt = NewClickEvent(Guid.NewGuid());
        var msg = NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(evt));

        var disposition = await processor.ProcessAsync(msg, dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(handler.Handled);
        Assert.Equal(evt.ShortenedUrlId, handler.Handled[0].ShortenedUrlId);
        Assert.Empty(delays);
        Assert.Empty(dlq.Messages);
    }

    [Fact]
    public async Task ProcessAsync_TransientFailuresThenSuccess_RetriesWithExponentialBackoffAndReturnsAck()
    {
        var handler = new FakeClickEventHandler(attempt => attempt switch
        {
            1 => new InvalidOperationException("Transient DB error"),
            2 => new TaskCanceledException("Simulated command timeout"),
            _ => null
        });
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = CreateProcessor(handler, delays, new ClickConsumerOptions
        {
            MaxRetryAttempts = 3,
            InitialRetryDelayMs = 500,
            MaxRetryDelayMs = 10_000
        });
        var msg = NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(NewClickEvent(Guid.NewGuid())));

        var disposition = await processor.ProcessAsync(msg, dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }, delays);
        Assert.Empty(dlq.Messages);
    }

    [Fact]
    public async Task ProcessAsync_AllRetriesExhausted_RoutesToDlqWithMetadataAndReturnsAck()
    {
        var longMessage = new string('x', 700);
        var handler = new FakeClickEventHandler(_ => new TimeoutException(longMessage));
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = CreateProcessor(handler, delays, new ClickConsumerOptions
        {
            MaxRetryAttempts = 3,
            InitialRetryDelayMs = 500,
            MaxRetryDelayMs = 10_000
        });
        var rawBody = JsonSerializer.SerializeToUtf8Bytes(NewClickEvent(Guid.NewGuid()));
        var msg = NewIncomingMessage(rawBody, exchange: "clicks", routingKey: "click.created");

        var disposition = await processor.ProcessAsync(msg, dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(4, handler.CallCount);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000) },
            delays);

        var dead = Assert.Single(dlq.Messages);
        Assert.Equal(rawBody, dead.Body.ToArray());
        Assert.Equal("processing-failed", dead.Reason);
        Assert.Equal("clicks", dead.OriginalExchange);
        Assert.Equal("click.created", dead.OriginalRoutingKey);
        Assert.Equal(RabbitMqClickEventPublisher.Queue, dead.OriginalQueue);
        Assert.Equal(3, dead.RetryAttempts);
        Assert.Equal(typeof(TimeoutException).FullName, dead.ErrorType);
        Assert.Equal(500, dead.ErrorMessage.Length);
        Assert.Equal(_clock.UtcNow, dead.FailedAt);
    }

    [Fact]
    public async Task ProcessAsync_ZeroMaxRetries_RoutesDirectlyToDlqWithoutDelay()
    {
        var handler = new FakeClickEventHandler(_ => new InvalidOperationException("Boom"));
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = CreateProcessor(handler, delays, new ClickConsumerOptions { MaxRetryAttempts = 0 });
        var msg = NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(NewClickEvent(Guid.NewGuid())));

        var disposition = await processor.ProcessAsync(msg, dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(delays);
        var dead = Assert.Single(dlq.Messages);
        Assert.Equal("processing-failed", dead.Reason);
        Assert.Equal(0, dead.RetryAttempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{bad")]
    [InlineData("null")]
    [InlineData("{\"ShortenedUrlId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task ProcessAsync_PoisonPayloads_RouteImmediatelyToDlqWithoutRetry(string? rawPayload)
    {
        var handler = new FakeClickEventHandler();
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = CreateProcessor(handler, delays);
        var bytes = rawPayload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(rawPayload);
        var msg = NewIncomingMessage(bytes);

        var disposition = await processor.ProcessAsync(msg, dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(delays);

        var dead = Assert.Single(dlq.Messages);
        Assert.Equal("invalid-payload", dead.Reason);
        Assert.Equal(0, dead.RetryAttempts);
        Assert.Equal(bytes, dead.Body.ToArray());
    }

    [Fact]
    public async Task ProcessAsync_WhenDlqPublishFails_ReturnsNackRequeue()
    {
        var handler = new FakeClickEventHandler(_ => new InvalidOperationException("DB down"));
        var failingDlq = new FakeDeadLetterPublisher { ShouldThrow = true };
        var processor = CreateProcessor(handler, new List<TimeSpan>(), new ClickConsumerOptions { MaxRetryAttempts = 1 });

        var poisonDisposition = await processor.ProcessAsync(NewIncomingMessage(Encoding.UTF8.GetBytes("{bad")), failingDlq);
        var failedDisposition = await processor.ProcessAsync(
            NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(NewClickEvent(Guid.NewGuid()))),
            failingDlq);

        Assert.Equal(MessageDisposition.NackRequeue, poisonDisposition);
        Assert.Equal(MessageDisposition.NackRequeue, failedDisposition);
    }

    [Fact]
    public async Task ProcessAsync_WhenCancelledDuringHandler_ReturnsNackRequeueWithoutRetryOrDlq()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeClickEventHandler(_ =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = CreateProcessor(handler, delays);
        var msg = NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(NewClickEvent(Guid.NewGuid())));

        var disposition = await processor.ProcessAsync(msg, dlq, cts.Token);

        Assert.Equal(MessageDisposition.NackRequeue, disposition);
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(delays);
        Assert.Empty(dlq.Messages);
    }

    [Fact]
    public async Task ProcessAsync_WhenCancelledDuringBackoffDelay_StopsRetryingAndReturnsNackRequeueWithoutDlq()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeClickEventHandler(_ => new InvalidOperationException("Transient error"));
        var dlq = new FakeDeadLetterPublisher();
        var delays = new List<TimeSpan>();
        var processor = new ClickEventMessageProcessor(
            handler,
            Options.Create(new ClickConsumerOptions { MaxRetryAttempts = 3, InitialRetryDelayMs = 500 }),
            _clock,
            NullLogger<ClickEventMessageProcessor>.Instance,
            (delay, ct) =>
            {
                delays.Add(delay);
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var msg = NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(NewClickEvent(Guid.NewGuid())));

        var disposition = await processor.ProcessAsync(msg, dlq, cts.Token);

        Assert.Equal(MessageDisposition.NackRequeue, disposition);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(delays);
        Assert.Empty(dlq.Messages);
    }

    [Fact]
    public void BuildDeadLetterProperties_SetsPersistentDeliveryModeAndDiagnosticHeaders()
    {
        var failedAt = new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.Zero);
        var msg = new DeadLetterMessage(
            Encoding.UTF8.GetBytes("{}"),
            "processing-failed",
            "clicks",
            "click.created",
            "clicks",
            3,
            "System.TimeoutException",
            "DB timeout",
            failedAt);

        var props = RabbitMqDeadLetterPublisher.BuildDeadLetterProperties(msg);

        Assert.Equal(DeliveryModes.Persistent, props.DeliveryMode);
        Assert.Equal("application/json", props.ContentType);
        Assert.NotNull(props.Headers);
        Assert.Equal("processing-failed", props.Headers!["x-dead-letter-reason"]);
        Assert.Equal("clicks", props.Headers["x-original-exchange"]);
        Assert.Equal("click.created", props.Headers["x-original-routing-key"]);
        Assert.Equal("clicks", props.Headers["x-original-queue"]);
        Assert.Equal(3, props.Headers["x-retry-count"]);
        Assert.Equal("System.TimeoutException", props.Headers["x-error-type"]);
        Assert.Equal("DB timeout", props.Headers["x-error-message"]);
        Assert.Equal(failedAt.ToString("O"), props.Headers["x-failed-at"]);
    }

    [Fact]
    public async Task ScopedClickEventHandler_WhenAggregationFailsThenRetrySucceeds_RollsBackAndPersistsSingleClick()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        var ownerId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using (var setupCtx = new AppDbContext(dbOptions))
        {
            await setupCtx.Database.EnsureCreatedAsync();
            await setupCtx.Users.AddAsync(new User
            {
                Id = ownerId,
                Username = "worker-test",
                Email = "worker-test@example.com",
                PasswordHash = "hash",
                CreatedAt = _clock.UtcNow
            });
            await setupCtx.ShortLinks.AddAsync(new ShortenedUrl
            {
                Id = linkId,
                Slug = "wrk1",
                Url = "https://example.com/wrk1",
                OwnerId = ownerId,
                CreatedAt = _clock.UtcNow
            });
            await setupCtx.SaveChangesAsync();
        }

        var upsertAttempt = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(dbOptions));
        services.AddScoped<IClickRepository>(sp => new FailOnceUpsertClickRepository(
            new ClickRepository(sp.GetRequiredService<AppDbContext>()),
            () => Interlocked.Increment(ref upsertAttempt) == 1));
        services.AddScoped<ClickIngestionService>();
        services.AddScoped<ClickAggregationService>();
        await using var provider = services.BuildServiceProvider();

        var handler = new ScopedClickEventHandler(provider.GetRequiredService<IServiceScopeFactory>());
        var delays = new List<TimeSpan>();
        var dlq = new FakeDeadLetterPublisher();
        var processor = CreateProcessor(handler, delays, new ClickConsumerOptions
        {
            MaxRetryAttempts = 2,
            InitialRetryDelayMs = 250,
            MaxRetryDelayMs = 1000
        });

        var evt = NewClickEvent(linkId);
        var disposition = await processor.ProcessAsync(NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(evt)), dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(2, upsertAttempt);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(250) }, delays);
        Assert.Empty(dlq.Messages);

        await using var verifyCtx = new AppDbContext(dbOptions);
        Assert.Equal(1, await verifyCtx.Clicks.CountAsync(c => c.ShortenedUrlId == linkId));
        var aggregates = await verifyCtx.ClickAggregates.Where(a => a.ShortenedUrlId == linkId).ToListAsync();
        Assert.Equal(2, aggregates.Count);
        Assert.All(aggregates, a => Assert.Equal(1L, a.Count));
    }

    [Fact]
    public async Task ScopedClickEventHandler_WhenAggregationFailsThenRetrySucceeds_RollsBackAndIncrementsClickCountOnce()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        var ownerId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using (var setupCtx = new AppDbContext(dbOptions))
        {
            await setupCtx.Database.EnsureCreatedAsync();
            await setupCtx.Users.AddAsync(new User
            {
                Id = ownerId,
                Username = "worker-counter",
                Email = "worker-counter@example.com",
                PasswordHash = "hash",
                CreatedAt = _clock.UtcNow
            });
            await setupCtx.ShortLinks.AddAsync(new ShortenedUrl
            {
                Id = linkId,
                Slug = "cnt1",
                Url = "https://example.com/cnt1",
                OwnerId = ownerId,
                CreatedAt = _clock.UtcNow
            });
            await setupCtx.SaveChangesAsync();
        }

        var upsertAttempt = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(dbOptions));
        services.AddScoped<IShortLinkClickCounter, ShortLinkRepository>();
        services.AddScoped<IClickRepository>(sp => new FailOnceUpsertClickRepository(
            new ClickRepository(sp.GetRequiredService<AppDbContext>()),
            () => Interlocked.Increment(ref upsertAttempt) == 1));
        services.AddScoped<ClickIngestionService>();
        services.AddScoped<ClickAggregationService>();
        await using var provider = services.BuildServiceProvider();

        var handler = new ScopedClickEventHandler(provider.GetRequiredService<IServiceScopeFactory>());
        var delays = new List<TimeSpan>();
        var dlq = new FakeDeadLetterPublisher();
        var processor = CreateProcessor(handler, delays, new ClickConsumerOptions
        {
            MaxRetryAttempts = 2,
            InitialRetryDelayMs = 250,
            MaxRetryDelayMs = 1000
        });

        var evt = NewClickEvent(linkId);
        var disposition = await processor.ProcessAsync(NewIncomingMessage(JsonSerializer.SerializeToUtf8Bytes(evt)), dlq);

        Assert.Equal(MessageDisposition.Ack, disposition);
        Assert.Equal(2, upsertAttempt);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(250) }, delays);
        Assert.Empty(dlq.Messages);

        await using var verifyCtx = new AppDbContext(dbOptions);
        Assert.Equal(1, await verifyCtx.Clicks.CountAsync(c => c.ShortenedUrlId == linkId));
        var updatedLink = await verifyCtx.ShortLinks.SingleAsync(l => l.Id == linkId);
        Assert.Equal(1L, updatedLink.ClickCount);
        var aggregates = await verifyCtx.ClickAggregates.Where(a => a.ShortenedUrlId == linkId).ToListAsync();
        Assert.Equal(2, aggregates.Count);
        Assert.All(aggregates, a => Assert.Equal(1L, a.Count));
    }

    [Fact]
    public async Task DiContainer_ResolvesClickIngestionServiceWithClickCounter_FromAddShortLinkPersistence()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        var ownerId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using (var setupCtx = new AppDbContext(dbOptions))
        {
            await setupCtx.Database.EnsureCreatedAsync();
            await setupCtx.Users.AddAsync(new User
            {
                Id = ownerId,
                Username = "worker-di",
                Email = "worker-di@example.com",
                PasswordHash = "hash",
                CreatedAt = _clock.UtcNow
            });
            await setupCtx.ShortLinks.AddAsync(new ShortenedUrl
            {
                Id = linkId,
                Slug = "di01",
                Url = "https://example.com/di01",
                OwnerId = ownerId,
                CreatedAt = _clock.UtcNow
            });
            await setupCtx.SaveChangesAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=shortlink;Username=shortlink"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddShortLinkPersistence(config);
        services.RemoveAll<AppDbContext>();
        services.AddScoped(_ => new AppDbContext(dbOptions));
        services.AddScoped<ClickIngestionService>();
        services.AddScoped<ClickAggregationService>();
        services.AddSingleton<IClickEventHandler, ScopedClickEventHandler>();

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IClickEventHandler>();

        await handler.HandleAsync(NewClickEvent(linkId));

        await using var verifyCtx = new AppDbContext(dbOptions);
        Assert.Equal(1, await verifyCtx.Clicks.CountAsync(c => c.ShortenedUrlId == linkId));
        var updatedLink = await verifyCtx.ShortLinks.SingleAsync(l => l.Id == linkId);
        Assert.Equal(1L, updatedLink.ClickCount);
        var aggregates = await verifyCtx.ClickAggregates.Where(a => a.ShortenedUrlId == linkId).ToListAsync();
        Assert.Equal(2, aggregates.Count);
        Assert.All(aggregates, a => Assert.Equal(1L, a.Count));
    }

    [Fact]
    public async Task ClickEventConsumer_WhenConnectionFails_RetriesWithExponentialBackoffUntilStopped()
    {
        var recordedDelays = new List<TimeSpan>();
        var connectAttempts = 0;
        var readyToStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = CreateProcessor(new FakeClickEventHandler(), new List<TimeSpan>());
        var options = Options.Create(new ClickConsumerOptions
        {
            InitialConnectDelayMs = 1000,
            MaxConnectDelayMs = 30_000
        });

        var consumer = new ClickEventConsumer(
            NullLogger<ClickEventConsumer>.Instance,
            processor,
            options,
            async ct =>
            {
                var attempt = Interlocked.Increment(ref connectAttempts);
                if (attempt <= 3)
                    throw new InvalidOperationException("Broker unreachable");

                readyToStop.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable");
            },
            (delay, _) =>
            {
                recordedDelays.Add(delay);
                return Task.CompletedTask;
            });

        await consumer.StartAsync(CancellationToken.None);
        await readyToStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await consumer.StopAsync(CancellationToken.None);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
            recordedDelays);
    }

    [Fact]
    public void ClickEventConsumer_WhenConnectionStringMissing_ThrowsInvalidOperationException()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var processor = CreateProcessor(new FakeClickEventHandler(), new List<TimeSpan>());

        Assert.Throws<InvalidOperationException>(() => new ClickEventConsumer(
            NullLogger<ClickEventConsumer>.Instance,
            processor,
            Options.Create(new ClickConsumerOptions()),
            emptyConfig));
    }

    [Fact]
    public async Task DiContainer_ResolvesProcessorAndConsumer_WithWorkerRegistrations()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=shortlink;Username=shortlink",
                ["RabbitMQ:ConnectionString"] = "amqp://shortlink@localhost:5672/"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddShortLinkPersistence(config);
        services.Configure<ClickConsumerOptions>(config.GetSection(ClickConsumerOptions.SectionName));
        services.AddScoped<ClickIngestionService>();
        services.AddScoped<ClickAggregationService>();
        services.AddSingleton<IClickEventHandler, ScopedClickEventHandler>();
        services.AddSingleton<ClickEventMessageProcessor>();
        services.AddSingleton<ClickEventConsumer>();

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ClickEventMessageProcessor>());
        Assert.NotNull(provider.GetRequiredService<ClickEventConsumer>());
    }

    private ClickEventMessageProcessor CreateProcessor(
        IClickEventHandler handler,
        List<TimeSpan> recordedDelays,
        ClickConsumerOptions? options = null)
        => new(
            handler,
            Options.Create(options ?? new ClickConsumerOptions()),
            _clock,
            NullLogger<ClickEventMessageProcessor>.Instance,
            (delay, _) =>
            {
                recordedDelays.Add(delay);
                return Task.CompletedTask;
            });

    private ClickEvent NewClickEvent(Guid shortenedUrlId) => new(
        shortenedUrlId,
        _clock.UtcNow,
        "203.0.113.10",
        "Mozilla/5.0",
        "https://ref.example",
        DeviceType.Desktop,
        "ID",
        "JK",
        "Jakarta");

    private static IncomingClickMessage NewIncomingMessage(
        byte[] body,
        string exchange = "clicks",
        string routingKey = "click.created",
        ulong deliveryTag = 1)
        => new(deliveryTag, body, exchange, routingKey);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeClickEventHandler : IClickEventHandler
    {
        private readonly Func<int, Exception?>? _exceptionFactory;

        public int CallCount { get; private set; }
        public List<ClickEvent> Handled { get; } = new();

        public FakeClickEventHandler(Func<int, Exception?>? exceptionFactory = null)
            => _exceptionFactory = exceptionFactory;

        public Task HandleAsync(ClickEvent clickEvent, CancellationToken ct = default)
        {
            CallCount++;
            var ex = _exceptionFactory?.Invoke(CallCount);
            if (ex is not null)
                return Task.FromException(ex);

            Handled.Add(clickEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeadLetterPublisher : IDeadLetterPublisher
    {
        public bool ShouldThrow { get; init; }
        public List<DeadLetterMessage> Messages { get; } = new();

        public Task PublishAsync(DeadLetterMessage message, CancellationToken ct = default)
        {
            if (ShouldThrow)
                throw new InvalidOperationException("DLQ unavailable");

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceUpsertClickRepository(IClickRepository inner, Func<bool> shouldFail) : IClickRepository
    {
        public Task AddAsync(Click click, CancellationToken ct = default) => inner.AddAsync(click, ct);
        public Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default) => inner.AddRangeAsync(clicks, ct);
        public Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default) => inner.AddAggregateAsync(aggregate, ct);
        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => inner.GetByShortLinkIdAsync(shortenedUrlId, ct);

        public Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default)
        {
            if (shouldFail())
                throw new InvalidOperationException("Transient aggregation failure");

            return inner.UpsertAggregatesAsync(aggregates, ct);
        }
    }
}
