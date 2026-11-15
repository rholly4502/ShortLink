using ShortLink.Core.Entities;

namespace ShortLink.Core.Abstractions;

/// <summary>Cache slug → ShortenedUrl (mis. Redis). Menyimpan entitas agar event klik bisa memakai Id.</summary>
public interface ISlugCache
{
    Task<ShortenedUrl?> GetAsync(string slug, CancellationToken ct = default);

    Task SetAsync(string slug, ShortenedUrl link, TimeSpan? ttl = null, CancellationToken ct = default);

    Task RemoveAsync(string slug, CancellationToken ct = default);
}
