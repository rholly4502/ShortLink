using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Core;

/// <summary>Domain logic untuk short link: create/resolve/validate/analytics agregasi.</summary>
public sealed class ShortLinkService
{
    private readonly IShortLinkRepository _repository;
    private readonly ISlugGenerator _slugGenerator;
    private readonly IUrlValidator _urlValidator;
    private readonly IClock _clock;

    public ShortLinkService(
        IShortLinkRepository repository,
        ISlugGenerator slugGenerator,
        IUrlValidator urlValidator,
        IClock clock)
    {
        _repository = repository;
        _slugGenerator = slugGenerator;
        _urlValidator = urlValidator;
        _clock = clock;
    }

    public async Task<ShortenedUrl> CreateAsync(
        Guid ownerId,
        string url,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        if (!_urlValidator.TryNormalize(url, out var normalized))
            throw new ArgumentException("URL tidak valid atau tidak aman.", nameof(url));

        string slug;
        do
        {
            slug = _slugGenerator.Generate();
        }
        while (await _repository.SlugExistsAsync(slug, ct));

        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Url = normalized.ToString(),
            OwnerId = ownerId,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = expiresAt
        };

        await _repository.AddAsync(link, ct);
        return link;
    }

    public async Task<ShortenedUrl?> ResolveAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var link = await _repository.GetBySlugAsync(slug, ct);
        if (link is null)
            return null;

        if (link.ExpiresAt is { } expiresAt && expiresAt <= _clock.UtcNow)
            return null;

        return link;
    }

    public async Task<List<ShortenedUrl>> ListForOwnerAsync(Guid ownerId, CancellationToken ct = default)
        => await _repository.GetByOwnerAsync(ownerId, ct);

    public async Task<ShortenedUrl?> GetForOwnerAsync(Guid ownerId, Guid id, CancellationToken ct = default)
    {
        var link = await _repository.GetByIdAsync(id, ct);
        return link is not null && link.OwnerId == ownerId ? link : null;
    }

    public async Task<ShortenedUrl> UpdateUrlAsync(
        Guid ownerId,
        Guid id,
        string url,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        if (!_urlValidator.TryNormalize(url, out var normalized))
            throw new ArgumentException("URL tidak valid atau tidak aman.", nameof(url));

        var link = await _repository.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException("Short link tidak ditemukan.");
        if (link.OwnerId != ownerId)
            throw new UnauthorizedAccessException();

        link.Url = normalized.ToString();
        link.ExpiresAt = expiresAt;
        await _repository.UpdateAsync(link, ct);
        return link;
    }

    public async Task DeleteByOwnerAsync(Guid ownerId, Guid id, CancellationToken ct = default)
    {
        var link = await _repository.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException("Short link tidak ditemukan.");
        if (link.OwnerId != ownerId)
            throw new UnauthorizedAccessException();

        await _repository.DeleteAsync(link, ct);
    }

    /// <summary>Agregasi klik murni (tanpa dependensi infra) per periode/device/country.</summary>
    public static IReadOnlyList<ClickAggregate> AggregateClicks(IEnumerable<Click> clicks, AggregatePeriod period)
    {
        ArgumentNullException.ThrowIfNull(clicks);

        return clicks
            .Where(c => c.ClickedAt != default)
            .GroupBy(c => (Bucket(c.ClickedAt, period), c.Device, c.Country))
            .Select(g => new ClickAggregate
            {
                Id = Guid.NewGuid(),
                PeriodStart = g.Key.Item1,
                Period = period,
                Device = g.Key.Item2,
                Country = g.Key.Item3,
                Count = g.LongCount()
            })
            .OrderBy(a => a.PeriodStart)
            .ThenBy(a => a.Device)
            .ToList();
    }

    private static DateTimeOffset Bucket(DateTimeOffset time, AggregatePeriod period)
        => period switch
        {
            AggregatePeriod.Hourly => new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, 0, 0, time.Offset),
            _ => new DateTimeOffset(time.Year, time.Month, time.Day, 0, 0, 0, time.Offset)
        };
}
