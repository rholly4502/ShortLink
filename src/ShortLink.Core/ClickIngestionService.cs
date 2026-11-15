using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;

namespace ShortLink.Core;

/// <summary>
/// Mengubah ClickEvent (dari RabbitMQ) menjadi entitas Click dan mempersist-nya.
/// Logika murni, dapat diuji tanpa RabbitMQ (P4.1).
/// </summary>
public sealed class ClickIngestionService
{
    private readonly IClickRepository _clicks;

    public ClickIngestionService(IClickRepository clicks) => _clicks = clicks;

    public async Task PersistAsync(ClickEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var click = new Click
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = evt.ShortenedUrlId,
            IpAddress = evt.IpAddress,
            UserAgent = evt.UserAgent,
            Referrer = evt.Referrer,
            Device = evt.Device,
            Country = evt.Country,
            Region = evt.Region,
            City = evt.City,
            ClickedAt = evt.OccurredAt
        };

        await _clicks.AddAsync(click, ct);
    }
}
