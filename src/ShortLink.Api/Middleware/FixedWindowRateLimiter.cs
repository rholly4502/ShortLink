using Microsoft.Extensions.Logging;
using ShortLink.Infrastructure.Caching;

namespace ShortLink.Api.Middleware;

/// <summary>Fixed-window rate limiter di atas Redis (atomic INCR + EXPIRE), fail-open bila Redis tidak tersedia.</summary>
public sealed class FixedWindowRateLimiter
{
    private readonly IRedisStringStore _store;
    private readonly ILogger<FixedWindowRateLimiter>? _logger;

    public FixedWindowRateLimiter(IRedisStringStore store, ILogger<FixedWindowRateLimiter>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<bool> IsAllowedAsync(string key, int limit, TimeSpan window, CancellationToken ct = default)
    {
        try
        {
            var count = await _store.IncrementAsync(key, window, ct);
            return count <= limit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "Redis rate-limit store unavailable; failing open.");
            return true;
        }
    }
}
