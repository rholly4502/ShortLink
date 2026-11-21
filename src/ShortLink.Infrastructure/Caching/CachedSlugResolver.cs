using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Caching;

/// <summary>
/// Resolver slug dengan komposisi cache-first + fallback PG:
/// cek cache; miss atau cache error → tanya repository (PG); kalau ketemu &amp; belum lewat, isi cache (best-effort).
/// Mengembalikan entitas ShortenedUrl agar caller bisa memakai Id untuk event klik.
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

    public async Task<ShortenedUrl?> ResolveAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        ShortenedUrl? cached = null;
        try
        {
            cached = await _cache.GetAsync(slug, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Graceful fallback ke PostgreSQL bila Redis sementara tidak tersedia.
        }

        if (cached is not null)
        {
            if (cached.ExpiresAt is { } cachedExpiresAt && cachedExpiresAt <= _clock.UtcNow)
                return null;

            return cached;
        }

        var link = await _repository.GetBySlugAsync(slug, ct);
        if (link is null)
            return null;

        if (link.ExpiresAt is { } expiresAt && expiresAt <= _clock.UtcNow)
            return null;

        var effectiveTtl = _ttl;
        if (link.ExpiresAt is { } exp)
        {
            var remaining = exp - _clock.UtcNow;
            if (remaining < effectiveTtl)
                effectiveTtl = remaining;
        }

        try
        {
            await _cache.SetAsync(slug, link, effectiveTtl, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best-effort cache backfill; jangan gagalkan redirect bila Redis sementara down.
        }

        return link;
    }
}
