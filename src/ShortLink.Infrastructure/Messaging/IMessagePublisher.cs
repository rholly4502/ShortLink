namespace ShortLink.Infrastructure.Messaging;

public interface IMessagePublisher
{
    Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default);
}
