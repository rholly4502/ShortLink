using StackExchange.Redis;

namespace ShortLink.Infrastructure.Caching;

/// <summary>Implementasi IRedisStringStore di atas StackExchange.Redis.</summary>
public sealed class RedisStringStore : IRedisStringStore, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _mux;

    public static ConfigurationOptions BuildConfigurationOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return options;
    }

    public RedisStringStore(string connectionString)
    {
        _mux = ConnectionMultiplexer.Connect(BuildConfigurationOptions(connectionString));
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var value = await _mux.GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
        => _mux.GetDatabase().StringSetAsync(key, value, ttl);

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        => _mux.GetDatabase().KeyDeleteAsync(key);

    private const string IncrementScript =
        "local current = redis.call('INCR', KEYS[1])\n" +
        "if current == 1 then\n" +
        "  redis.call('EXPIRE', KEYS[1], ARGV[1])\n" +
        "end\n" +
        "return current\n";

    public async Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var db = _mux.GetDatabase();
        var result = await db.ScriptEvaluateAsync(
            IncrementScript,
            new RedisKey[] { key },
            new RedisValue[] { (int)ttl.TotalSeconds });
        return (long)result;
    }

    public ValueTask DisposeAsync() => _mux.DisposeAsync();
}
