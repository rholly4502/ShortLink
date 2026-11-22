using System.Net;
using System.Text.Json;
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
    private const int CreateLimit = 4;
    private const int ApiLimit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static (RedisRateLimitMiddleware Middleware, FakeRedisStringStore Store, Func<bool> NextCalled) Build(FakeRedisStringStore? store = null)
    {
        store ??= new FakeRedisStringStore();
        var options = new RateLimitOptions
        {
            AuthLimit = AuthLimit,
            AuthWindowSeconds = 45,
            CreateLimit = CreateLimit,
            CreateWindowSeconds = 50,
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

    private static DefaultHttpContext NewRequestContext(string method, string path, string ip = "1.2.3.4")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = new PathString(path);
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
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
            CreateLimit = CreateLimit,
            CreateWindowSeconds = 50,
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

    [Theory]
    [InlineData("/api/links")]
    [InlineData("/api/links/")]
    [InlineData("/API/LINKS")]
    public async Task Invoke_PostApiLinks_TracksDedicatedCreateBucket(string path)
    {
        var (mw, store, nextCalled) = Build();
        var ctx = NewRequestContext(HttpMethods.Post, path);

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(1L, store.Counts["rl:create:1.2.3.4"]);
        Assert.False(store.Counts.ContainsKey("rl:api:1.2.3.4"));
    }

    [Fact]
    public async Task Invoke_PostApiLinks_OverCreateLimit_Returns429WithRetryAfterAndJsonError()
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:create:1.2.3.4"] = CreateLimit;
        var ctx = NewRequestContext(HttpMethods.Post, "/api/links");

        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal("50", ctx.Response.Headers.RetryAfter.ToString());
        Assert.Equal("application/json; charset=utf-8", ctx.Response.ContentType);

        var body = ReadBody(ctx);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));
    }

    [Fact]
    public async Task Invoke_CreateBucket_IsIsolatedFromApiAuthAndRedirect()
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:api:1.2.3.4"] = 999;
        store.Counts["rl:auth:1.2.3.4"] = 999;
        store.Counts["rl:redirect:1.2.3.4"] = 999;

        var createCtx = NewRequestContext(HttpMethods.Post, "/api/links");
        await mw.InvokeAsync(createCtx);

        Assert.True(nextCalled());
        Assert.Equal(200, createCtx.Response.StatusCode);
        Assert.Equal(1L, store.Counts["rl:create:1.2.3.4"]);

        var (mw2, store2, nextCalled2) = Build();
        store2.Counts["rl:create:1.2.3.4"] = 999;

        await mw2.InvokeAsync(NewRequestContext(HttpMethods.Get, "/api/links"));
        Assert.True(nextCalled2());
        Assert.Equal(1L, store2.Counts["rl:api:1.2.3.4"]);

        await mw2.InvokeAsync(NewRequestContext(HttpMethods.Put, "/api/links/00000000-0000-0000-0000-000000000001"));
        Assert.Equal(2L, store2.Counts["rl:api:1.2.3.4"]);
    }

    [Theory]
    [InlineData("GET", "/api/links")]
    [InlineData("PUT", "/api/links/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/links/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/links/00000000-0000-0000-0000-000000000001")]
    public async Task Invoke_NonCreateApiLinksRequests_UseApiBucket(string method, string path)
    {
        var (mw, store, nextCalled) = Build();
        var ctx = NewRequestContext(method, path);

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(1L, store.Counts["rl:api:1.2.3.4"]);
        Assert.False(store.Counts.ContainsKey("rl:create:1.2.3.4"));
    }

    [Theory]
    [InlineData("GET", "/api/health")]
    [InlineData("HEAD", "/api/health")]
    [InlineData("GET", "/API/HEALTH")]
    [InlineData("GET", "/api/health/")]
    public async Task Invoke_HealthProbe_GetAndHead_BypassRateLimitWithoutTouchingStore(string method, string path)
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:api:1.2.3.4"] = ApiLimit + 10;
        var ctx = NewRequestContext(method, path);

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Empty(store.Increments);
    }

    [Theory]
    [InlineData("POST", "/api/health")]
    [InlineData("DELETE", "/api/health")]
    [InlineData("GET", "/api/health/extra")]
    public async Task Invoke_HealthNonProbeOrSubpath_RemainsRateLimited(string method, string path)
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:api:1.2.3.4"] = ApiLimit;
        var ctx = NewRequestContext(method, path);

        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Single(store.Increments);
        Assert.Equal("rl:api:1.2.3.4", store.Increments[0].Key);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("api:victim")]
    [InlineData("203.0.113.9:8080")]
    [InlineData("2001:db8::1%eth0")]
    [InlineData("2001:db8::1%123")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Invoke_MalformedOrInjectedXForwardedFor_FallsBackToRemoteIp(string headerValue)
    {
        var (mw, store, nextCalled) = Build();
        var ctx = NewRequestContext(HttpMethods.Get, "/api/links", "1.2.3.4");
        ctx.Request.Headers["X-Forwarded-For"] = headerValue;

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(1L, store.Counts["rl:api:1.2.3.4"]);
        Assert.DoesNotContain(store.Counts.Keys, k => k.Contains(headerValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invoke_XForwardedFor_CanonicalizesIpv6AndIpv4MappedIpv6()
    {
        var (mw, store, _) = Build();

        var v6Expanded = NewRequestContext(HttpMethods.Get, "/api/links");
        v6Expanded.Request.Headers["X-Forwarded-For"] = "2001:0DB8:0000:0000:0000:0000:0000:0001, 10.0.0.1";
        await mw.InvokeAsync(v6Expanded);

        var v6Compressed = NewRequestContext(HttpMethods.Get, "/api/links");
        v6Compressed.Request.Headers["X-Forwarded-For"] = "2001:db8::1";
        await mw.InvokeAsync(v6Compressed);

        Assert.Equal(2L, store.Counts["rl:api:2001:db8::1"]);

        var mappedV4 = NewRequestContext(HttpMethods.Get, "/api/links");
        mappedV4.Request.Headers["X-Forwarded-For"] = "::ffff:203.0.113.9";
        await mw.InvokeAsync(mappedV4);

        var plainV4 = NewRequestContext(HttpMethods.Get, "/api/links");
        plainV4.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        await mw.InvokeAsync(plainV4);

        Assert.Equal(2L, store.Counts["rl:api:203.0.113.9"]);
    }

    [Fact]
    public async Task Invoke_RemoteIpIpv4MappedIpv6_CanonicalizesToIpv4()
    {
        var (mw, store, nextCalled) = Build();
        var ctx = NewRequestContext(HttpMethods.Get, "/api/links");
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:198.51.100.7");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled());
        Assert.Equal(1L, store.Counts["rl:api:198.51.100.7"]);
    }

    [Fact]
    public async Task Invoke_HeadOverLimit_Returns429WithEmptyBody()
    {
        var (mw, store, nextCalled) = Build();
        store.Counts["rl:api:1.2.3.4"] = ApiLimit;
        var ctx = NewRequestContext(HttpMethods.Head, "/api/links");

        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers.RetryAfter.ToString());
        Assert.Equal(0L, ctx.Response.Body.Length);
    }

    [Fact]
    public void RateLimitOptions_Defaults_CreateIsStricterThanApiAndWindowsClampToAtLeastOneSecond()
    {
        var defaults = new RateLimitOptions();
        Assert.True(defaults.AuthLimit < defaults.CreateLimit);
        Assert.True(defaults.CreateLimit < defaults.ApiLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.CreateWindow);

        var invalid = new RateLimitOptions
        {
            AuthWindowSeconds = 0,
            CreateWindowSeconds = -5,
            ApiWindowSeconds = 0,
            RedirectWindowSeconds = -10
        };
        Assert.Equal(TimeSpan.FromSeconds(1), invalid.AuthWindow);
        Assert.Equal(TimeSpan.FromSeconds(1), invalid.CreateWindow);
        Assert.Equal(TimeSpan.FromSeconds(1), invalid.ApiWindow);
        Assert.Equal(TimeSpan.FromSeconds(1), invalid.RedirectWindow);
    }
}
