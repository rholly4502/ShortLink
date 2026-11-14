using ShortLink.Core.Events;

namespace ShortLink.Core.Abstractions;

public interface IClickEventPublisher
{
    Task PublishAsync(ClickEvent clickEvent, CancellationToken ct = default);
}
