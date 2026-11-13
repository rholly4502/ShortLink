using ShortLink.Core.Entities;

namespace ShortLink.Core.Abstractions;

public interface IClickRepository
{
    Task AddAsync(Click click, CancellationToken ct = default);

    Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default);

    Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default);
}
