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

        Assert.Null(await resolver.ResolveAsync(null!));
        Assert.Null(await resolver.ResolveAsync("  "));
    }

    [Fact]
    public async Task Resolve_WhenCacheGetThrows_FallsBackToRepository()
    {
        var cache = new FaultySlugCache(throwOnGet: true, throwOnSet: false);
        var repo = new FakeRepo();
        repo.BySlug["abc123"] = NewLink("abc123");
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("abc123");

        Assert.NotNull(link);
        Assert.Equal("https://example.com/abc123", link!.Url);
    }

    [Fact]
    public async Task Resolve_WhenCacheSetThrows_StillReturnsLinkFromRepository()
    {
        var cache = new FaultySlugCache(throwOnGet: false, throwOnSet: true);
        var repo = new FakeRepo();
        repo.BySlug["abc123"] = NewLink("abc123");
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("abc123");

        Assert.NotNull(link);
        Assert.Equal("https://example.com/abc123", link!.Url);
    }

    [Fact]
    public async Task Resolve_WhenCacheGetCancelled_PropagatesOperationCanceledException()
    {
        var cache = new FaultySlugCache(throwOnGet: true, throwOnSet: false, throwCancellation: true);
        var repo = new ThrowingRepository();
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync("abc123", cts.Token));
    }

    [Fact]
    public async Task Resolve_WhenCacheSetCancelled_PropagatesOperationCanceledException()
    {
        var cache = new FaultySlugCache(throwOnGet: false, throwOnSet: true, throwCancellation: true);
        var repo = new FakeRepo();
        repo.BySlug["abc123"] = NewLink("abc123");
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync("abc123", cts.Token));
    }

    [Fact]
    public async Task Resolve_CachedLinkAlreadyExpired_ReturnsNullWithoutQueryingRepo()
    {
        var cache = new FakeSlugCache
        {
            Links = { ["old"] = NewLink("old", expiresAt: _clock.UtcNow.AddMinutes(-5)) }
        };
        var repo = new ThrowingRepository();
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("old");

        Assert.Null(link);
    }

    [Fact]
    public async Task Resolve_ExpiringSoonLink_ClampsCacheTtlToRemainingTime()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["soon"] = NewLink("soon", expiresAt: _clock.UtcNow.AddMinutes(30));
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("soon");

        Assert.NotNull(link);
        Assert.Equal(TimeSpan.FromMinutes(30), cache.LastSetTtl);
    }

    [Fact]
    public async Task Resolve_LongLivedLink_UsesConfiguredDefaultTtl()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["long"] = NewLink("long", expiresAt: _clock.UtcNow.AddDays(30));
        var configuredTtl = TimeSpan.FromHours(12);
        var resolver = new CachedSlugResolver(cache, repo, _clock, configuredTtl);

        var link = await resolver.ResolveAsync("long");

        Assert.NotNull(link);
        Assert.Equal(configuredTtl, cache.LastSetTtl);
    }

    [Fact]
    public async Task Resolve_LinkExpiringInLessThanOneMillisecond_ReturnsLinkWithoutCaching()
    {
        var cache = new FakeSlugCache();
        var repo = new FakeRepo();
        repo.BySlug["tiny"] = NewLink("tiny", expiresAt: _clock.UtcNow.AddTicks(100));
        var resolver = new CachedSlugResolver(cache, repo, _clock, TimeSpan.FromHours(12));

        var link = await resolver.ResolveAsync("tiny");

        Assert.NotNull(link);
        Assert.Equal("https://example.com/tiny", link!.Url);
        Assert.Empty(cache.Links);
        Assert.Null(cache.LastSetTtl);
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

    private sealed class FaultySlugCache : ISlugCache
    {
        private readonly bool _throwOnGet;
        private readonly bool _throwOnSet;
        private readonly bool _throwCancellation;

        public FaultySlugCache(bool throwOnGet, bool throwOnSet, bool throwCancellation = false)
        {
            _throwOnGet = throwOnGet;
            _throwOnSet = throwOnSet;
            _throwCancellation = throwCancellation;
        }

        public Task<ShortenedUrl?> GetAsync(string slug, CancellationToken ct = default)
        {
            if (_throwOnGet)
            {
                if (_throwCancellation)
                    throw new OperationCanceledException(ct);
                throw new InvalidOperationException("Redis unavailable");
            }
            return Task.FromResult<ShortenedUrl?>(null);
        }

        public Task SetAsync(string slug, ShortenedUrl link, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            if (_throwOnSet)
            {
                if (_throwCancellation)
                    throw new OperationCanceledException(ct);
                throw new InvalidOperationException("Redis unavailable");
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string slug, CancellationToken ct = default) => Task.CompletedTask;
    }
}

public class RedisStringStoreTests
{
    [Fact]
    public void BuildConfigurationOptions_SetsAbortOnConnectFailFalse_AndPreservesSettings()
    {
        var options = RedisStringStore.BuildConfigurationOptions("redis:6379,password=secret");

        Assert.False(options.AbortOnConnectFail);
        Assert.Equal("secret", options.Password);
        var endpoint = Assert.IsType<System.Net.DnsEndPoint>(Assert.Single(options.EndPoints));
        Assert.Equal("redis", endpoint.Host);
        Assert.Equal(6379, endpoint.Port);
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
    public TimeSpan? LastSetTtl { get; private set; }

    public Task<ShortenedUrl?> GetAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(Links.TryGetValue(slug, out var l) ? l : null);

    public Task SetAsync(string slug, ShortenedUrl link, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        LastSetTtl = ttl;
        Links[slug] = link;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string slug, CancellationToken ct = default)
    {
        Links.Remove(slug);
        return Task.CompletedTask;
    }
}
