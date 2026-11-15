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
    public async Task GetAsync_WhenStoreHasInvalidJson_ReturnsNull()
    {
        var store = new FakeRedisStringStore { Values = { ["slug:abc123"] = "not-json" } };
        var cache = new RedisSlugCache(store, TimeSpan.FromHours(12));

        var result = await cache.GetAsync("abc123");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_WritesPrefixedKeyWithTtl()
    {
        var store = new FakeRedisStringStore();
        var ttl = TimeSpan.FromHours(24);
        var cache = new RedisSlugCache(store, ttl);
        var link = NewLink("abc123");

        await cache.SetAsync("abc123", link, ttl);

        var entry = Assert.Single(store.Entries);
        Assert.Equal("slug:abc123", entry.Key);
        Assert.Equal(ttl, entry.Ttl);
    }

    [Fact]
    public async Task SetAsync_WhenNoTtlProvided_UsesDefault()
    {
        var store = new FakeRedisStringStore();
        var cache = new RedisSlugCache(store, TimeSpan.FromHours(12));

        await cache.SetAsync("abc123", NewLink("abc123"));

        Assert.Equal(TimeSpan.FromHours(12), store.Entries.Single().Ttl);
    }

    [Fact]
    public async Task RoundTrip_PersistsShortenedUrl()
    {
        var store = new FakeRedisStringStore();
        var cache = new RedisSlugCache(store, TimeSpan.FromHours(12));
        var link = NewLink("abc123");

        await cache.SetAsync("abc123", link);
        var result = await cache.GetAsync("abc123");

        Assert.NotNull(result);
        Assert.Equal(link.Id, result!.Id);
        Assert.Equal(link.Slug, result.Slug);
        Assert.Equal(link.Url, result.Url);
    }

    private static ShortenedUrl NewLink(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Url = "https://example.com/" + slug,
        OwnerId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = null
    };
}

public class CachedSlugResolverTests
{
    private readonly FixedClock _clock = new();

    [Fact]
    public async Task Resolve_CacheHit_ReturnsLinkWithoutQueryingRepo()
    {
        var cache = new FakeSlugCache { Links = { ["abc123"] = NewLink("abc123") } };
        var repo = new ThrowingRepository();
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("abc123");

        Assert.NotNull(link);
        Assert.Equal("abc123", link!.Slug);
    }

    [Fact]
    public async Task Resolve_CacheMiss_QueriesRepoAndPopulatesCache()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["abc123"] = NewLink("abc123");
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("abc123");

        Assert.NotNull(link);
        Assert.True(cache.Links.ContainsKey("abc123"));
    }

    [Fact]
    public async Task Resolve_MissingInRepo_ReturnsNullAndDoesNotCache()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("nope");

        Assert.Null(link);
        Assert.Empty(cache.Links);
    }

    [Fact]
    public async Task Resolve_ExpiredLink_ReturnsNullAndDoesNotCache()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["old"] = NewLink("old", expiresAt: _clock.UtcNow.AddHours(-1));
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("old");

        Assert.Null(link);
        Assert.Empty(cache.Links);
    }

    [Fact]
    public async Task Resolve_NullOrWhitespace_ReturnsNull()
    {
        var resolver = new CachedSlugResolver(new FakeSlugCache(), new FakeRepo(), _clock, TimeSpan.FromHours(12));

        Assert.Null(await resolver.ResolveAsync(null));
        Assert.Null(await resolver.ResolveAsync("  "));
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

    public Dictionary<string, long> Counts { get; } = new();
    public List<(string Key, TimeSpan Ttl)> Increments { get; } = new();

    public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        Increments.Add((key, ttl));
        var next = Counts.TryGetValue(key, out var c) ? c + 1 : 1;
        Counts[key] = next;
        return Task.FromResult(next);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Values.Remove(key));
}

public sealed class FakeSlugCache : ISlugCache
{
    public Dictionary<string, ShortenedUrl> Links { get; } = new();

    public Task<ShortenedUrl?> GetAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(Links.TryGetValue(slug, out var l) ? l : null);

    public Task SetAsync(string slug, ShortenedUrl link, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        Links[slug] = link;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string slug, CancellationToken ct = default)
    {
        Links.Remove(slug);
        return Task.CompletedTask;
    }
}
