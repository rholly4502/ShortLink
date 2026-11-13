using ShortLink.Core.Entities;

namespace ShortLink.Core.Abstractions;

public interface IShortLinkRepository
{
    Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default);

    Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default);

    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);

    Task AddAsync(ShortenedUrl link, CancellationToken ct = default);

    Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default);

    Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default);
}
