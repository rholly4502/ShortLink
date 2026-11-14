using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ShortLink.Infrastructure.Messaging;

/// <summary>Publisher RabbitMQ sungguhan. Menjaga koneksi & channel, mendeklarasikan exchange + queue.</summary>
public sealed class RabbitMqMessagePublisher : IMessagePublisher, IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RabbitMqMessagePublisher(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<RabbitMqMessagePublisher> CreateAsync(string connectionString, CancellationToken ct = default)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        var connection = await factory.CreateConnectionAsync(ct);
        var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync(RabbitMqClickEventPublisher.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(RabbitMqClickEventPublisher.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(RabbitMqClickEventPublisher.Queue, RabbitMqClickEventPublisher.Exchange, "#", cancellationToken: ct);

        return new RabbitMqMessagePublisher(connection, channel);
    }

    public Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default)
        => _channel.BasicPublishAsync(exchange, routingKey, mandatory: false, body: body, cancellationToken: ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
