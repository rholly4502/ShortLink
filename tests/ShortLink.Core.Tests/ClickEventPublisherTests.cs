using System.Text.Json;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Messaging;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

public class ClickEventPublisherTests
{
    private sealed class FakeMessagePublisher : IMessagePublisher
    {
        public string? Exchange;
        public string? RoutingKey;
        public byte[]? Body;

        public Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            Exchange = exchange;
            RoutingKey = routingKey;
            Body = body.ToArray();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PublishAsync_SerializesClickEvent_AndPublishesToExchange()
    {
        var fake = new FakeMessagePublisher();
        var publisher = new RabbitMqClickEventPublisher(fake);

        var evt = new ClickEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "1.2.3.4",
            "Mozilla/5.0",
            "https://ref.example",
            DeviceType.Desktop,
            "ID",
            null,
            null);

        await publisher.PublishAsync(evt);

        Assert.Equal("clicks", fake.Exchange);
        Assert.Equal("click.created", fake.RoutingKey);
        Assert.NotNull(fake.Body);

        var roundtrip = JsonSerializer.Deserialize<ClickEvent>(fake.Body!);
        Assert.NotNull(roundtrip);
        Assert.Equal(evt.ShortenedUrlId, roundtrip.ShortenedUrlId);
        Assert.Equal(evt.IpAddress, roundtrip.IpAddress);
        Assert.Equal(evt.Referrer, roundtrip.Referrer);
        Assert.Equal(evt.Device, roundtrip.Device);
    }

    [Fact]
    public async Task PublishAsync_WithNullOptionals_StillSerializes()
    {
        var fake = new FakeMessagePublisher();
        var publisher = new RabbitMqClickEventPublisher(fake);

        var evt = new ClickEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, null, null, null, DeviceType.Other, null, null, null);
        await publisher.PublishAsync(evt);

        var roundtrip = JsonSerializer.Deserialize<ClickEvent>(fake.Body!);
        Assert.NotNull(roundtrip);
        Assert.Null(roundtrip.IpAddress);
    }
}
