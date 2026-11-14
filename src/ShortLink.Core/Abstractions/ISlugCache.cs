namespace ShortLink.Core.Abstractions;

/// <summary>Cache slug → URL asli (mis. Redis).</summary>
public interface ISlugCache
{
    Task<string?> GetAsync(string slug, CancellationToken ct = default);

    Task SetAsync(string slug, string url, TimeSpan? ttl = null, CancellationToken ct = default);
}
