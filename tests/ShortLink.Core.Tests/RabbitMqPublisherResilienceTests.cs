using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Messaging;

namespace ShortLink.Core.Tests;

public sealed class RabbitMqPublisherResilienceTests
{
    [Fact]
    public void CreateConnectionFactory_ConfiguresManualRecoveryAndTimeouts()
    {
        var defaultFactory = RabbitMqMessagePublisher.CreateConnectionFactory("amqp://shortlink:secret@rabbit:5672/");
        Assert.False(defaultFactory.AutomaticRecoveryEnabled);
        Assert.Equal(RabbitMqMessagePublisher.DefaultConnectTimeout, defaultFactory.RequestedConnectionTimeout);
        Assert.Equal("rabbit", defaultFactory.Uri.Host);
        Assert.Equal(5672, defaultFactory.Uri.Port);

        var customTimeout = TimeSpan.FromSeconds(7);
        var customFactory = RabbitMqMessagePublisher.CreateConnectionFactory("amqp://rabbit:5672/", customTimeout);
        Assert.Equal(customTimeout, customFactory.RequestedConnectionTimeout);

        Assert.ThrowsAny<ArgumentException>(() => RabbitMqMessagePublisher.CreateConnectionFactory("   "));
        Assert.Throws<UriFormatException>(() => RabbitMqMessagePublisher.CreateConnectionFactory("not a uri"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RabbitMqMessagePublisher.CreateConnectionFactory("amqp://rabbit:5672/", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RabbitMqMessagePublisher.CreateConnectionFactory("amqp://rabbit:5672/", TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task PublishAsync_LazyConnectsOnceAndReusesOpenSession()
    {
        var clock = new MutableClock();
        var connectCount = 0;
        var session = new FakePublishSession();

        await using var publisher = new RabbitMqMessagePublisher(
            _ =>
            {
                connectCount++;
                return Task.FromResult<IRabbitMqPublishSession>(session);
            },
            clock);

        Assert.Equal(0, connectCount);

        await publisher.PublishAsync("clicks", "click.created", Encoding.UTF8.GetBytes("{\"n\":1}"));
        await publisher.PublishAsync("clicks", "click.created", Encoding.UTF8.GetBytes("{\"n\":2}"));

        Assert.Equal(1, connectCount);
        Assert.Equal(2, session.Published.Count);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task PublishAsync_WhenSessionCloses_DisposesStaleSessionAndReconnectsImmediately()
    {
        var clock = new MutableClock();
        var first = new FakePublishSession { ThrowOnDispose = true };
        var second = new FakePublishSession();
        var sessions = new Queue<IRabbitMqPublishSession>([first, second]);
        var connectCount = 0;

        await using var publisher = new RabbitMqMessagePublisher(
            _ =>
            {
                connectCount++;
                return Task.FromResult(sessions.Dequeue());
            },
            clock,
            reconnectCooldown: TimeSpan.FromSeconds(30));

        await publisher.PublishAsync("clicks", "click.created", new byte[] { 1 });
        first.IsOpen = false;

        await publisher.PublishAsync("clicks", "click.created", new byte[] { 2 });

        Assert.Equal(2, connectCount);
        Assert.True(first.Disposed);
        Assert.Single(first.Published);
        Assert.Single(second.Published);
    }

    [Fact]
    public async Task PublishAsync_WhenConnectFails_SuppressesReconnectDuringCooldownAndRecoversAfterCooldown()
    {
        var clock = new MutableClock();
        var connectCount = 0;
        var brokerUp = false;
        var recoveredSession = new FakePublishSession();

        await using var publisher = new RabbitMqMessagePublisher(
            _ =>
            {
                connectCount++;
                if (!brokerUp)
                    throw new InvalidOperationException("Broker unreachable");
                return Task.FromResult<IRabbitMqPublishSession>(recoveredSession);
            },
            clock,
            reconnectCooldown: TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 1 }));
        Assert.Equal(1, connectCount);

        clock.Advance(TimeSpan.FromSeconds(4));
        var cooldownEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 2 }));
        Assert.Contains("cooldown", cooldownEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, connectCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        brokerUp = true;
        await publisher.PublishAsync("clicks", "click.created", new byte[] { 3 });

        Assert.Equal(2, connectCount);
        Assert.Single(recoveredSession.Published);
    }

    [Fact]
    public async Task PublishAsync_WhenMidPublishFails_ResetsSessionEntersCooldownAndRecoversAfterCooldown()
    {
        var clock = new MutableClock();
        var first = new FakePublishSession
        {
            PublishHook = (_, _) => throw new TaskCanceledException("Simulated publish timeout")
        };
        var second = new FakePublishSession();
        var connectCount = 0;

        await using var publisher = new RabbitMqMessagePublisher(
            _ =>
            {
                connectCount++;
                return Task.FromResult<IRabbitMqPublishSession>(connectCount == 1 ? first : second);
            },
            clock,
            reconnectCooldown: TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 1 }));
        Assert.Equal(1, connectCount);
        Assert.True(first.Disposed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 2 }));
        Assert.Equal(1, connectCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        await publisher.PublishAsync("clicks", "click.created", new byte[] { 3 });

        Assert.Equal(2, connectCount);
        Assert.Single(second.Published);
    }

    [Fact]
    public async Task PublishAsync_WhenCallerCancelled_PropagatesAndPreservesHealthySessionWithoutCooldown()
    {
        var clock = new MutableClock();
        var connectCount = 0;
        var session = new FakePublishSession();

        await using var publisher = new RabbitMqMessagePublisher(
            _ =>
            {
                connectCount++;
                return Task.FromResult<IRabbitMqPublishSession>(session);
            },
            clock,
            reconnectCooldown: TimeSpan.FromSeconds(10));

        // Establish healthy session first
        await publisher.PublishAsync("clicks", "click.created", new byte[] { 1 });

        // Cancel during mid-publish on the open session
        using var cts = new CancellationTokenSource();
        session.PublishHook = (_, token) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 2 }, cts.Token));
        Assert.False(session.Disposed);

        // Pre-cancelled token before acquiring gate also propagates without resetting session
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 3 }, cts.Token));
        Assert.False(session.Disposed);

        session.PublishHook = null;
        await publisher.PublishAsync("clicks", "click.created", new byte[] { 4 });

        Assert.Equal(1, connectCount);
        Assert.Equal(2, session.Published.Count);
    }

    [Fact]
    public async Task PublishAsync_ConcurrentCalls_AreSerializedOnUnderlyingSession()
    {
        var clock = new MutableClock();
        var activePublishes = 0;
        var maxConcurrent = 0;
        var session = new FakePublishSession
        {
            PublishHook = async (_, _) =>
            {
                var current = Interlocked.Increment(ref activePublishes);
                UpdateMax(ref maxConcurrent, current);
                await Task.Yield();
                await Task.Delay(5);
                Interlocked.Decrement(ref activePublishes);
            }
        };

        await using var publisher = new RabbitMqMessagePublisher(
            _ => Task.FromResult<IRabbitMqPublishSession>(session),
            clock);

        var tasks = Enumerable.Range(0, 12)
            .Select(i => publisher.PublishAsync("clicks", "click.created", new[] { (byte)i }))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(12, session.Published.Count);
    }

    [Fact]
    public async Task DisposeAsync_DisposesSessionIsIdempotentAndRejectsSubsequentPublish()
    {
        var clock = new MutableClock();
        var session = new FakePublishSession();
        var publisher = new RabbitMqMessagePublisher(
            _ => Task.FromResult<IRabbitMqPublishSession>(session),
            clock);

        await publisher.PublishAsync("clicks", "click.created", new byte[] { 1 });

        await publisher.DisposeAsync();
        await publisher.DisposeAsync();

        Assert.True(session.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            publisher.PublishAsync("clicks", "click.created", new byte[] { 2 }));
    }

    [Fact]
    public async Task DefaultDiRegistration_ResolvesClickEventPublisherLazilyWithoutConnectingToBroker()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "shortlink-rmq-lazy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(webRoot);
                    builder.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["RabbitMQ:ConnectionString"] = "amqp://guest:guest@127.0.0.1:59999/"
                        });
                    });
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(new FakeRedisStringStore());
                    });
                });

            using var scope = factory.Services.CreateScope();
            var messagePublisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();
            var clickPublisher = scope.ServiceProvider.GetRequiredService<IClickEventPublisher>();

            Assert.IsType<RabbitMqMessagePublisher>(messagePublisher);
            Assert.IsType<RabbitMqClickEventPublisher>(clickPublisher);
        }
        finally
        {
            if (Directory.Exists(webRoot))
                Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RedirectEndpoint_WhenRabbitMqUnavailableThenRecovers_AlwaysReturns302AndResumesClickPublishing()
    {
        var clock = new MutableClock();
        var brokerUp = false;
        var connectCount = 0;
        var session = new FakePublishSession();

        var webRoot = Path.Combine(Path.GetTempPath(), "shortlink-rmq-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);

        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "resilient1",
            Url = "https://example.com/resilient-target",
            OwnerId = Guid.NewGuid(),
            CreatedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow.AddDays(1)
        };
        var links = new InMemoryShortLinkRepository();
        await links.AddAsync(link);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(webRoot);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IClock>();
                        services.AddSingleton<IClock>(clock);

                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(new FakeRedisStringStore());

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(links);

                        services.RemoveAll<IMessagePublisher>();
                        services.AddSingleton<IMessagePublisher>(new RabbitMqMessagePublisher(
                            _ =>
                            {
                                connectCount++;
                                if (!brokerUp)
                                    throw new InvalidOperationException("Broker unreachable");
                                return Task.FromResult<IRabbitMqPublishSession>(session);
                            },
                            clock,
                            reconnectCooldown: TimeSpan.FromSeconds(5)));
                    });
                });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            // Phase 1: broker down -> first request attempts connect, second request fast-fails in cooldown; both still 302.
            var firstRes = await client.GetAsync("/resilient1");
            Assert.Equal(HttpStatusCode.Found, firstRes.StatusCode);
            Assert.Equal("https://example.com/resilient-target", firstRes.Headers.Location?.ToString());
            Assert.Equal(1, connectCount);
            Assert.Empty(session.Published);

            clock.Advance(TimeSpan.FromSeconds(2));
            var secondRes = await client.GetAsync("/resilient1");
            Assert.Equal(HttpStatusCode.Found, secondRes.StatusCode);
            Assert.Equal("https://example.com/resilient-target", secondRes.Headers.Location?.ToString());
            Assert.Equal(1, connectCount);
            Assert.Empty(session.Published);

            // Phase 2: advance past cooldown and recover broker -> still 302 and click event is published.
            clock.Advance(TimeSpan.FromSeconds(3));
            brokerUp = true;
            var thirdRes = await client.GetAsync("/resilient1");
            Assert.Equal(HttpStatusCode.Found, thirdRes.StatusCode);
            Assert.Equal("https://example.com/resilient-target", thirdRes.Headers.Location?.ToString());
            Assert.Equal(2, connectCount);

            var published = Assert.Single(session.Published);
            Assert.Equal(RabbitMqClickEventPublisher.Exchange, published.Exchange);
            Assert.Equal(RabbitMqClickEventPublisher.RoutingKey, published.RoutingKey);

            var evt = JsonSerializer.Deserialize<ClickEvent>(published.Body);
            Assert.NotNull(evt);
            Assert.Equal(link.Id, evt!.ShortenedUrlId);
        }
        finally
        {
            if (Directory.Exists(webRoot))
                Directory.Delete(webRoot, recursive: true);
        }
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        int initial, computed;
        do
        {
            initial = Volatile.Read(ref target);
            if (candidate <= initial)
                return;
            computed = candidate;
        }
        while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 21, 19, 30, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class FakePublishSession : IRabbitMqPublishSession
    {
        public bool IsOpen { get; set; } = true;
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; init; }
        public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? PublishHook { get; set; }
        public List<(string Exchange, string RoutingKey, byte[] Body)> Published { get; } = new();

        public async Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            if (PublishHook is not null)
                await PublishHook(body, ct);

            Published.Add((exchange, routingKey, body.ToArray()));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (ThrowOnDispose)
                throw new InvalidOperationException("Simulated dispose error");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryShortLinkRepository : IShortLinkRepository
    {
        private readonly Dictionary<string, ShortenedUrl> _bySlug = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(_bySlug.TryGetValue(slug, out var link) ? link : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_bySlug.Values.FirstOrDefault(l => l.Id == id));

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(_bySlug.Values.Where(l => l.OwnerId == ownerId).ToList());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(_bySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            _bySlug[link.Slug] = link;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            _bySlug[link.Slug] = link;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            _bySlug.Remove(link.Slug);
            return Task.CompletedTask;
        }
    }
}
