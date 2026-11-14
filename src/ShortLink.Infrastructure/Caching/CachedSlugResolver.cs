using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Caching;

/// <summary>
/// Resolver slug dengan komposisi cache-first + fallback PG:
/// cek cache; miss → tanya repository (PG); kalau ketemu & belum lewat, isi cache.
/// </summary>
public sealed class CachedSlugResolver
{
    private readonly ISlugCache _cache;
    private readonly IShortLinkRepository _repository;
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;

    public CachedSlugResolver(ISlugCache cache, IShortLinkRepository repository, IClock clock, TimeSpan ttl)
    {
        _cache = cache;
        _repository = repository;
        _clock = clock;
        _ttl = ttl;
    }

    public async Task<string?> ResolveUrlAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var cached = await _cache.GetAsync(slug, ct);
        if (cached is not null)
            return cached;

        var link = await _repository.GetBySlugAsync(slug, ct);
        if (link is null)
            return null;

        if (link.ExpiresAt is { } expiresAt && expiresAt <= _clock.UtcNow)
            return null;

        await _cache.SetAsync(slug, link.Url, _ttl, ct);
        return link.Url;
    }
}
