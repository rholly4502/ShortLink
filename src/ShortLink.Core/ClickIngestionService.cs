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
    public const int MaxIpAddressLength = 45;
    public const int MaxUserAgentLength = 512;
    public const int MaxReferrerLength = 2048;
    public const int MaxCountryLength = 2;
    public const int MaxRegionLength = 100;
    public const int MaxCityLength = 100;

    private readonly IClickRepository _clicks;
    private readonly IShortLinkClickCounter? _clickCounter;

    public ClickIngestionService(IClickRepository clicks, IShortLinkClickCounter? clickCounter = null)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        _clicks = clicks;
        _clickCounter = clickCounter;
    }

    public async Task<Click> PersistAsync(ClickEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var click = new Click
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = evt.ShortenedUrlId,
            IpAddress = SanitizeAndTruncate(evt.IpAddress, MaxIpAddressLength),
            UserAgent = SanitizeAndTruncate(evt.UserAgent, MaxUserAgentLength),
            Referrer = SanitizeAndTruncate(evt.Referrer, MaxReferrerLength),
            Device = evt.Device,
            Country = SanitizeAndTruncate(evt.Country, MaxCountryLength),
            Region = SanitizeAndTruncate(evt.Region, MaxRegionLength),
            City = SanitizeAndTruncate(evt.City, MaxCityLength),
            ClickedAt = evt.OccurredAt
        };

        await _clicks.AddAsync(click, ct);
        if (_clickCounter is not null)
            await _clickCounter.IncrementClickCountAsync(click.ShortenedUrlId, 1, ct);

        return click;
    }

    private static string? SanitizeAndTruncate(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var sanitized = value.Contains('\0')
            ? value.Replace("\0", string.Empty, StringComparison.Ordinal)
            : value;

        if (sanitized.Length <= maxLength)
            return sanitized;

        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(sanitized[length - 1]))
            length--;

        return sanitized[..length];
    }
}
