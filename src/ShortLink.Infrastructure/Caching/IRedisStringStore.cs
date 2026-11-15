namespace ShortLink.Infrastructure.Caching;

/// <summary>
/// Seam tipis di atas Redis (IDatabase) agar RedisSlugCache bisa diuji tanpa server Redis.
/// </summary>
public interface IRedisStringStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);

    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Atomic INCR; set EXPIRE saat pertama kali (kembali 1). Menghasilkan count baru.</summary>
    Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default);
}
