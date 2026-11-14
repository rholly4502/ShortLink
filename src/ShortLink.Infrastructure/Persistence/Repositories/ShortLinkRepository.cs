using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class ShortLinkRepository : IShortLinkRepository
{
    private readonly AppDbContext _db;

    public ShortLinkRepository(AppDbContext db) => _db = db;

    public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => _db.ShortLinks.SingleOrDefaultAsync(l => l.Slug == slug, ct);

    public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.ShortLinks.SingleOrDefaultAsync(l => l.Id == id, ct);

    public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
        => _db.ShortLinks.Where(l => l.OwnerId == ownerId).ToListAsync(ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
        => _db.ShortLinks.AnyAsync(l => l.Slug == slug, ct);

    public async Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
    {
        await _db.ShortLinks.AddAsync(link, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default)
    {
        _db.ShortLinks.Update(link);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default)
    {
        _db.ShortLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
    }
}
