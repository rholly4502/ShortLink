using StackExchange.Redis;

namespace ShortLink.Infrastructure.Caching;

/// <summary>Implementasi IRedisStringStore di atas StackExchange.Redis.</summary>
public sealed class RedisStringStore : IRedisStringStore, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _mux;

    public RedisStringStore(string connectionString)
    {
        _mux = ConnectionMultiplexer.Connect(connectionString);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var value = await _mux.GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
        => _mux.GetDatabase().StringSetAsync(key, value, ttl);

    public ValueTask DisposeAsync() => _mux.DisposeAsync();
}
