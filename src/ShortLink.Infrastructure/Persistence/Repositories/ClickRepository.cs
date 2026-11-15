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

    public async Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default)
    {
        var list = aggregates.ToList();
        if (list.Count == 0)
            return;

        var linkIds = list.Select(a => a.ShortenedUrlId).Distinct().ToList();
        var existing = await _db.ClickAggregates
            .Where(a => linkIds.Contains(a.ShortenedUrlId))
            .ToListAsync(ct);

        foreach (var agg in list)
        {
            var match = existing.FirstOrDefault(e =>
                e.ShortenedUrlId == agg.ShortenedUrlId &&
                e.PeriodStart == agg.PeriodStart &&
                e.Period == agg.Period &&
                e.Device == agg.Device &&
                e.Country == agg.Country);

            if (match is not null)
            {
                match.Count += agg.Count;
            }
            else
            {
                var added = new ClickAggregate
                {
                    Id = Guid.NewGuid(),
                    ShortenedUrlId = agg.ShortenedUrlId,
                    PeriodStart = agg.PeriodStart,
                    Period = agg.Period,
                    Device = agg.Device,
                    Country = agg.Country,
                    Count = agg.Count
                };
                _db.ClickAggregates.Add(added);
                existing.Add(added);
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
