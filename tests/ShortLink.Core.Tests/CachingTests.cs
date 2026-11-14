using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Infrastructure.Caching;

namespace ShortLink.Core.Tests;

public class RedisSlugCacheTests
{
    [Fact]
    public async Task GetAsync_WhenStoreReturnsNull_ReturnsNull()
    {
        var store = new FakeRedisStringStore();
        var cache = new RedisSlugCache(store, TimeSpan.FromHours(12));

        var result = await cache.GetAsync("abc123");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_WhenStoreHasValue_ReturnsValue()
    {
        var store = new FakeRedisStringStore { Values = { ["slug:abc123"] = "https://example.com" } };
        var cache = new RedisSlugCache(store, TimeSpan.FromHours(12));

        var result = await cache.GetAsync("abc123");

        Assert.Equal("https://example.com", result);
    }

    [Fact]
    public async Task SetAsync_WritesPrefixedKeyWithTtl()
    {
        var store = new FakeRedisStringStore();
        var ttl = TimeSpan.FromHours(24);
        var cache = new RedisSlugCache(store, ttl);

        await cache.SetAsync("abc123", "https://example.com", ttl);

        var entry = Assert.Single(store.Entries);
        Assert.Equal("slug:abc123", entry.Key);
        Assert.Equal("https://example.com", entry.Value);
        Assert.Equal(ttl, entry.Ttl);
    }

    [Fact]
    public async Task SetAsync_WhenNoTtlProvided_UsesDefault()
    {
        var store = new FakeRedisStringStore();
        var cache = new RedisSlugCache(store, TimeSpan.FromHours(12));

        await cache.SetAsync("abc123", "https://example.com");

        Assert.Equal(TimeSpan.FromHours(12), store.Entries.Single().Ttl);
    }
}

public class CachedSlugResolverTests
{
    private readonly FixedClock _clock = new();

    [Fact]
    public async Task Resolve_CacheHit_ReturnsUrlWithoutQueryingRepo()
    {
        var cache = new FakeSlugCache { Values = { ["abc123"] = "https://example.com" } };
        var repo = new ThrowingRepository();
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var url = await resolver.ResolveUrlAsync("abc123");

        Assert.Equal("https://example.com", url);
    }

    [Fact]
    public async Task Resolve_CacheMiss_QueriesRepoAndPopulatesCache()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["abc123"] = NewLink("abc123");
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var url = await resolver.ResolveUrlAsync("abc123");

        Assert.Equal("https://example.com/abc123", url);
        Assert.True(cache.Values.ContainsKey("abc123"));
    }

    [Fact]
    public async Task Resolve_MissingInRepo_ReturnsNullAndDoesNotCache()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var url = await resolver.ResolveUrlAsync("nope");

        Assert.Null(url);
        Assert.Empty(cache.Values);
    }

    [Fact]
    public async Task Resolve_ExpiredLink_ReturnsNullAndDoesNotCache()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["old"] = NewLink("old", expiresAt: _clock.UtcNow.AddHours(-1));
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var url = await resolver.ResolveUrlAsync("old");

        Assert.Null(url);
        Assert.Empty(cache.Values);
    }

    private static ShortenedUrl NewLink(string slug, DateTimeOffset? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Url = "https://example.com/" + slug,
        OwnerId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt
    };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeRepo : IShortLinkRepository
    {
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var l) ? l : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<ShortenedUrl?>(null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(new List<ShortenedUrl>());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingRepository : IShortLinkRepository
    {
        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => throw new InvalidOperationException("repo should not be queried on cache hit");
        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default)
            => throw new InvalidOperationException();
    }
}

public sealed class FakeRedisStringStore : IRedisStringStore
{
    public Dictionary<string, string> Values { get; } = new();
    public List<(string Key, string Value, TimeSpan Ttl)> Entries { get; } = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        Entries.Add((key, value, ttl));
        Values[key] = value;
        return Task.CompletedTask;
    }
}

public sealed class FakeSlugCache : ISlugCache
{
    public Dictionary<string, string> Values { get; } = new();

    public Task<string?> GetAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(Values.TryGetValue(slug, out var v) ? v : null);

    public Task SetAsync(string slug, string url, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        Values[slug] = url;
        return Task.CompletedTask;
    }
}
