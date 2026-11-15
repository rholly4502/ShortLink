using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Persistence.Repositories;

public sealed class ClickRepository : IClickRepository
{
    private readonly AppDbContext _db;

    public ClickRepository(AppDbContext db) => _db = db;

    public async Task AddAsync(Click click, CancellationToken ct = default)
    {
        await _db.Clicks.AddAsync(click, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default)
    {
        await _db.Clicks.AddRangeAsync(clicks, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default)
    {
        await _db.ClickAggregates.AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
        => _db.ClickAggregates.Where(a => a.ShortenedUrlId == shortenedUrlId).ToListAsync(ct);
}
