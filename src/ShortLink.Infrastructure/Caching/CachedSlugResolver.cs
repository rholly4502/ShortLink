using Microsoft.Extensions.Logging;
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
    private readonly ILogger<CachedSlugResolver>? _logger;

    public CachedSlugResolver(
        ISlugCache cache,
        IShortLinkRepository repository,
        IClock clock,
        TimeSpan ttl,
        ILogger<CachedSlugResolver>? logger = null)
    {
        _cache = cache;
        _repository = repository;
        _clock = clock;
        _ttl = ttl;
        _logger = logger;
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
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "Redis cache read failed for slug '{Slug}'; falling back to database.", slug);
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

        var now = _clock.UtcNow;
        if (link.ExpiresAt is { } expiresAt && expiresAt <= now)
            return null;

        var effectiveTtl = _ttl;
        if (link.ExpiresAt is { } exp)
        {
            var remaining = exp - now;
            if (remaining < effectiveTtl)
                effectiveTtl = remaining;
        }

        if (effectiveTtl >= TimeSpan.FromMilliseconds(1))
        {
            try
            {
                await _cache.SetAsync(slug, link, effectiveTtl, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Redis cache write failed for slug '{Slug}'; returning database result without caching.", slug);
            }
        }

        return link;
    }
}
