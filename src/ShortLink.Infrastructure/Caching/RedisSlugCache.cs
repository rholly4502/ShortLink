using ShortLink.Core.Abstractions;

namespace ShortLink.Infrastructure.Caching;

/// <summary>Cache slug→URL di Redis dengan TTL.</summary>
public sealed class RedisSlugCache : ISlugCache
{
    private readonly IRedisStringStore _store;
    private readonly TimeSpan _defaultTtl;

    public RedisSlugCache(IRedisStringStore store, TimeSpan defaultTtl)
    {
        _store = store;
        _defaultTtl = defaultTtl;
    }

    public Task<string?> GetAsync(string slug, CancellationToken ct = default)
        => _store.GetAsync(Key(slug), ct);

    public Task SetAsync(string slug, string url, TimeSpan? ttl = null, CancellationToken ct = default)
        => _store.SetAsync(Key(slug), url, ttl ?? _defaultTtl, ct);

    private static string Key(string slug) => "slug:" + slug;
}
