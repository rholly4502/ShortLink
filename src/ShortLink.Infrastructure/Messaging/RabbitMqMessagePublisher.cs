using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ShortLink.Core;
using ShortLink.Core.Abstractions;

namespace ShortLink.Infrastructure.Messaging;

/// <summary>Sesi publish RabbitMQ (koneksi + channel) yang dapat dicek statusnya dan diganti saat putus.</summary>
public interface IRabbitMqPublishSession : IAsyncDisposable
{
    bool IsOpen { get; }
    Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default);
}

/// <summary>
/// Publisher RabbitMQ dengan lazy connect, sinkronisasi thread-safe,
/// auto-reconnect saat koneksi/channel tertutup, dan cooldown agar outage broker
/// tidak memblokir hot-path redirect.
/// </summary>
public sealed class RabbitMqMessagePublisher : IMessagePublisher, IAsyncDisposable
{
    public static readonly TimeSpan DefaultReconnectCooldown = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<IRabbitMqPublishSession>> _sessionFactory;
    private readonly IClock _clock;
    private readonly TimeSpan _reconnectCooldown;
    private readonly ILogger<RabbitMqMessagePublisher>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IRabbitMqPublishSession? _session;
    private DateTimeOffset? _nextReconnectAttemptAt;
    private int _disposed;

    public RabbitMqMessagePublisher(
        string connectionString,
        IClock? clock = null,
        TimeSpan? reconnectCooldown = null,
        TimeSpan? connectTimeout = null,
        ILogger<RabbitMqMessagePublisher>? logger = null)
        : this(
            CreateDefaultSessionFactory(
                CreateConnectionFactory(connectionString, connectTimeout),
                connectTimeout ?? DefaultConnectTimeout),
            clock ?? new SystemClock(),
            reconnectCooldown,
            logger)
    {
    }

    public RabbitMqMessagePublisher(
        Func<CancellationToken, Task<IRabbitMqPublishSession>> sessionFactory,
        IClock clock,
        TimeSpan? reconnectCooldown = null,
        ILogger<RabbitMqMessagePublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(clock);

        var effectiveCooldown = reconnectCooldown ?? DefaultReconnectCooldown;
        if (effectiveCooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconnectCooldown), "Reconnect cooldown tidak boleh negatif.");

        _sessionFactory = sessionFactory;
        _clock = clock;
        _reconnectCooldown = effectiveCooldown;
        _logger = logger;
    }

    public static ConnectionFactory CreateConnectionFactory(string connectionString, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var timeout = connectTimeout ?? DefaultConnectTimeout;
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "Connect timeout harus lebih besar dari nol.");

        return new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = timeout
        };
    }

    public static async Task<RabbitMqMessagePublisher> CreateAsync(string connectionString, CancellationToken ct = default)
    {
        var publisher = new RabbitMqMessagePublisher(connectionString);
        try
        {
            await publisher.WarmUpAsync(ct);
            return publisher;
        }
        catch
        {
            await publisher.DisposeAsync();
            throw;
        }
    }

    public async Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var session = await EnsureSessionLockedAsync(ct);
            try
            {
                await session.PublishAsync(exchange, routingKey, body, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ResetSessionLockedAsync();
                SetReconnectCooldownLocked();
                _logger?.LogWarning(ex,
                    "RabbitMQ publish failed for exchange '{Exchange}', routing key '{RoutingKey}'; session reset with {CooldownMs}ms reconnect cooldown.",
                    exchange, routingKey, (int)_reconnectCooldown.TotalMilliseconds);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task WarmUpAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await EnsureSessionLockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync();
        try
        {
            await ResetSessionLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IRabbitMqPublishSession> EnsureSessionLockedAsync(CancellationToken ct)
    {
        if (_session is { IsOpen: true })
            return _session;

        if (_session is not null)
            await ResetSessionLockedAsync();

        if (_nextReconnectAttemptAt is { } retryAt && _clock.UtcNow < retryAt)
            throw new InvalidOperationException($"RabbitMQ publisher is in reconnect cooldown until {retryAt:O}.");

        try
        {
            _session = await _sessionFactory(ct);
            _nextReconnectAttemptAt = null;
            return _session;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetReconnectCooldownLocked();
            _logger?.LogWarning(ex, "RabbitMQ connection setup failed; entering {CooldownMs}ms reconnect cooldown.",
                (int)_reconnectCooldown.TotalMilliseconds);
            throw;
        }
    }

    private void SetReconnectCooldownLocked()
        => _nextReconnectAttemptAt = _reconnectCooldown > TimeSpan.Zero ? _clock.UtcNow + _reconnectCooldown : null;

    private async Task ResetSessionLockedAsync()
    {
        var existing = _session;
        _session = null;
        if (existing is null)
            return;

        try
        {
            await existing.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Ignored error while disposing stale RabbitMQ publish session.");
        }
    }

    private static Func<CancellationToken, Task<IRabbitMqPublishSession>> CreateDefaultSessionFactory(
        ConnectionFactory factory, TimeSpan connectTimeout)
        => ct => RabbitMqPublishSession.ConnectAsync(factory, connectTimeout, ct);

    private sealed class RabbitMqPublishSession(IConnection connection, IChannel channel) : IRabbitMqPublishSession
    {
        private static readonly BasicProperties PersistentJsonProperties = new()
        {
            DeliveryMode = DeliveryModes.Persistent,
            ContentType = "application/json"
        };

        public bool IsOpen => connection.IsOpen && channel.IsOpen;

        public static async Task<IRabbitMqPublishSession> ConnectAsync(
            ConnectionFactory factory, TimeSpan connectTimeout, CancellationToken ct)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(connectTimeout);

            var conn = await factory.CreateConnectionAsync(connectCts.Token);
            IChannel? chan = null;
            try
            {
                chan = await conn.CreateChannelAsync(cancellationToken: connectCts.Token);
                await chan.ExchangeDeclareAsync(
                    RabbitMqClickEventPublisher.Exchange, ExchangeType.Topic, durable: true, cancellationToken: connectCts.Token);
                await chan.QueueDeclareAsync(
                    RabbitMqClickEventPublisher.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: connectCts.Token);
                await chan.QueueBindAsync(
                    RabbitMqClickEventPublisher.Queue, RabbitMqClickEventPublisher.Exchange, "#", cancellationToken: connectCts.Token);
                return new RabbitMqPublishSession(conn, chan);
            }
            catch
            {
                if (chan is not null)
                    try { await chan.DisposeAsync(); } catch { }
                try { await conn.DisposeAsync(); } catch { }
                throw;
            }
        }

        public Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default)
            => channel.BasicPublishAsync(
                exchange, routingKey, mandatory: false, basicProperties: PersistentJsonProperties, body: body, cancellationToken: ct).AsTask();

        public async ValueTask DisposeAsync()
        {
            try { await channel.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }
}
