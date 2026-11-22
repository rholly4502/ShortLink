namespace ShortLink.Core.Abstractions;

/// <summary>Atomic increment counter untuk ShortenedUrl.ClickCount pada jalur ingest klik asinkron.</summary>
public interface IShortLinkClickCounter
{
    Task IncrementClickCountAsync(Guid shortenedUrlId, long delta = 1, CancellationToken ct = default);
}
