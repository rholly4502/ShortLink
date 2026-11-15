using ShortLink.Core.Entities;

namespace ShortLink.Core.Abstractions;

public interface IClickRepository
{
    Task AddAsync(Click click, CancellationToken ct = default);

    Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default);

    Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default);

    Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default);

    /// <summary>Upsert agregasi: increment row eksisting (sesuai key unik) atau insert baru.</summary>
    Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default);
}
