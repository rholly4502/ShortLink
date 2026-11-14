namespace ShortLink.Infrastructure.Caching;

/// <summary>
/// Seam tipis di atas Redis (IDatabase) agar RedisSlugCache bisa diuji tanpa server Redis.
/// </summary>
public interface IRedisStringStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);
}
