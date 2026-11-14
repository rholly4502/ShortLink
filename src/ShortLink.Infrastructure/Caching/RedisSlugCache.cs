using System.Text.Json;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Caching;

/// <summary>Cache slug→ShortenedUrl di Redis dengan TTL (serialisasi JSON).</summary>
public sealed class RedisSlugCache : ISlugCache
{
    private readonly IRedisStringStore _store;
    private readonly TimeSpan _defaultTtl;

    public RedisSlugCache(IRedisStringStore store, TimeSpan defaultTtl)
    {
        _store = store;
        _defaultTtl = defaultTtl;
    }

    public async Task<ShortenedUrl?> GetAsync(string slug, CancellationToken ct = default)
    {
        var raw = await _store.GetAsync(Key(slug), ct);
        if (raw is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ShortenedUrl>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetAsync(string slug, ShortenedUrl link, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var raw = JsonSerializer.Serialize(link);
        return _store.SetAsync(Key(slug), raw, ttl ?? _defaultTtl, ct);
    }

    private static string Key(string slug) => "slug:" + slug;
}
