using ShortLink.Infrastructure.Caching;

namespace ShortLink.Api.Middleware;

/// <summary>Fixed-window rate limiter di atas Redis (atomic INCR + EXPIRE).</summary>
public sealed class FixedWindowRateLimiter
{
    private readonly IRedisStringStore _store;

    public FixedWindowRateLimiter(IRedisStringStore store) => _store = store;

    public async Task<bool> IsAllowedAsync(string key, int limit, TimeSpan window, CancellationToken ct = default)
    {
        var count = await _store.IncrementAsync(key, window, ct);
        return count <= limit;
    }
}
