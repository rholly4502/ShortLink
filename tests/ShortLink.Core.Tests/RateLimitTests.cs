using System.Net;
using Microsoft.AspNetCore.Http;
using ShortLink.Api.Middleware;
using ShortLink.Infrastructure.Caching;

namespace ShortLink.Core.Tests;

public class FixedWindowRateLimiterTests
{
    [Fact]
    public async Task IsAllowed_UnderLimit_ReturnsTrue()
    {
        var limiter = new FixedWindowRateLimiter(new FakeRedisStringStore());
        var allowed = await limiter.IsAllowedAsync("rl:api:1.2.3.4", 60, TimeSpan.FromSeconds(60));
        Assert.True(allowed);
    }

    [Fact]
    public async Task IsAllowed_AtLimit_ReturnsTrue()
    {
        var store = new FakeRedisStringStore();
        store.Counts["rl:api:1.2.3.4"] = 59;
        var limiter = new FixedWindowRateLimiter(store);
        var allowed = await limiter.IsAllowedAsync("rl:api:1.2.3.4", 60, TimeSpan.FromSeconds(60));
        Assert.True(allowed);
    }

    [Fact]
    public async Task IsAllowed_OverLimit_ReturnsFalse()
    {
        var store = new FakeRedisStringStore();
        store.Counts["rl:api:1.2.3.4"] = 60;
        var limiter = new FixedWindowRateLimiter(store);
        var allowed = await limiter.IsAllowedAsync("rl:api:1.2.3.4", 60, TimeSpan.FromSeconds(60));
        Assert.False(allowed);
    }

    [Fact]
    public async Task IsAllowed_IncrementsWithTtlAnd_key()
    {
        var store = new FakeRedisStringStore();
        var limiter = new FixedWindowRateLimiter(store);

        await limiter.IsAllowedAsync("rl:redirect:9.9.9.9", 10, TimeSpan.FromSeconds(5));

        var inc = Assert.Single(store.Increments);
        Assert.Equal("rl:redirect:9.9.9.9", inc.Key);
        Assert.Equal(TimeSpan.FromSeconds(5), inc.Ttl);
        Assert.Equal(1L, store.Counts["rl:redirect:9.9.9.9"]);
    }

    [Fact]
    public async Task IsAllowed_DifferentKeys_Independent()
    {
        var store = new FakeRedisStringStore();
        store.Counts["rl:api:1.1.1.1"] = 100;
        var limiter = new FixedWindowRateLimiter(store);

        var allowedOther = await limiter.IsAllowedAsync("rl:api:2.2.2.2", 60, TimeSpan.FromSeconds(60));

        Assert.True(allowedOther);
    }

    [Fact]
    public async Task IsAllowed_WhenStoreThrows_FailsOpenAndReturnsTrue()
    {
        var limiter = new FixedWindowRateLimiter(new FaultyRedisStringStore());

        var allowed = await limiter.IsAllowedAsync("rl:api:1.2.3.4", 60, TimeSpan.FromSeconds(60));

        Assert.True(allowed);
    }

    [Fact]
    public async Task IsAllowed_WhenCancelled_PropagatesOperationCanceledException()
    {
        var limiter = new FixedWindowRateLimiter(new FaultyRedisStringStore(throwCancellation: true));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            limiter.IsAllowedAsync("rl:api:1.2.3.4", 60, TimeSpan.FromSeconds(60), cts.Token));
    }
}

internal sealed class FaultyRedisStringStore : IRedisStringStore
{
    private readonly bool _throwCancellation;

    public FaultyRedisStringStore(bool throwCancellation = false)
    {
        _throwCancellation = throwCancellation;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Fail<string?>(ct);
    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) => Fail<object?>(ct);
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Fail<bool>(ct);
    public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default) => Fail<long>(ct);

    private Task<T> Fail<T>(CancellationToken ct)
    {
        if (_throwCancellation)
            throw new OperationCanceledException(ct);
        throw new InvalidOperationException("Redis unavailable");
    }
}

public class RedisRateLimitMiddlewareTests
{
    private const int AuthLimit = 2;
    private const int ApiLimit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static (RedisRateLimitMiddleware Middleware, FakeRedisStringStore Store, Func<bool> NextCalled) Build(FakeRedisStringStore? store = null)
    {
        store ??= new FakeRedisStringStore();
        var options = new RateLimitOptions
        {
            AuthLimit = AuthLimit,
            AuthWindowSeconds = 45,
            ApiLimit = ApiLimit,
            RedirectLimit = 3,
            ApiWindowSeconds = 60,
            RedirectWindowSeconds = 30
        };
        var limiter = new FixedWindowRateLimiter(store);
        var nextCalled = false;
        var middleware = new RedisRateLimitMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            limiter,
            options);
        return (middleware, store, () => nextCalled);
    }

    private static DefaultHttpContext NewApiContext(string ip = "1.2.3.4", string path = "/api/links")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = new PathString(path);
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return ctx;
    }

    [Fact]
    public async Task Invoke_ApiOverLimit_Returns429WithRetryAfter()
    {
        var (mw, store, _) = Build();
        store.Counts["rl:api:1.2.3.4"] = ApiLimit;

        var ctx = NewApiContext();
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Invoke_RedirectOverLimit_Returns429()
    {
        var (mw, store, _) = Build();
        store.Counts["rl:redirect:1.2.3.4"] = 3;

        var ctx = NewApiContext(path: "/abc123");
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/register")]
    [InlineData("/auth/refresh")]
    public async Task Invoke_AuthOverLimit_Returns429WithRetryAfter(string path)
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:auth:1.2.3.4"] = AuthLimit;

        var ctx = NewApiContext(path: path);
        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal("45", ctx.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Invoke_AuthUnderLimit_ForwardsToNextAndTracksAuthBucket()
    {
        var (mw, store, nextCalled) = Build();

        var ctx = NewApiContext(path: "/auth/login");
        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(1L, store.Counts["rl:auth:1.2.3.4"]);
    }

    [Fact]
    public async Task Invoke_AuthBucket_IsIsolatedFromRedirectAndApi()
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:redirect:1.2.3.4"] = 999;
        store.Counts["rl:api:1.2.3.4"] = 999;

        var ctx = NewApiContext(path: "/auth/login");
        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(1L, store.Counts["rl:auth:1.2.3.4"]);
    }

    [Fact]
    public void RateLimitOptions_Defaults_AuthIsStricterThanApiAndRedirect()
    {
        var defaults = new RateLimitOptions();

        Assert.True(defaults.AuthLimit < defaults.RedirectLimit);
        Assert.True(defaults.AuthLimit < defaults.ApiLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.AuthWindow);
    }

    [Fact]
    public async Task Invoke_UnderLimit_ForwardsToNext()
    {
        var (mw, _, nextCalled) = Build();

        var ctx = NewApiContext();
        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_UsesXForwardedFor_WhenPresent()
    {
        var (mw, store, _) = Build();
        var ctx = NewApiContext();
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.9, 10.0.0.1";

        await mw.InvokeAsync(ctx);

        Assert.Contains("rl:api:203.0.113.9", store.Counts.Keys);
    }

    [Fact]
    public async Task Invoke_WhenStoreThrows_FailsOpenAndForwardsToNext()
    {
        var options = new RateLimitOptions
        {
            AuthLimit = AuthLimit,
            AuthWindowSeconds = 45,
            ApiLimit = ApiLimit,
            RedirectLimit = 3,
            ApiWindowSeconds = 60,
            RedirectWindowSeconds = 30
        };
        var limiter = new FixedWindowRateLimiter(new FaultyRedisStringStore());
        var nextCalled = false;
        var mw = new RedisRateLimitMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, limiter, options);
        var ctx = NewApiContext();

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.Equal(200, ctx.Response.StatusCode);
    }
}
