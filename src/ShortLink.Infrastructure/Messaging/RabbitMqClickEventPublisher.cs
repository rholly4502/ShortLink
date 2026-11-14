using System.Text.Json;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Events;

namespace ShortLink.Infrastructure.Messaging;

public sealed class RabbitMqClickEventPublisher : IClickEventPublisher
{
    public const string Exchange = "clicks";
    public const string Queue = "clicks";
    public const string RoutingKey = "click.created";

    private readonly IMessagePublisher _publisher;

    public RabbitMqClickEventPublisher(IMessagePublisher publisher)
    {
        _publisher = publisher;
    }

    public Task PublishAsync(ClickEvent clickEvent, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(clickEvent);
        return _publisher.PublishAsync(Exchange, RoutingKey, body, ct);
    }
}
