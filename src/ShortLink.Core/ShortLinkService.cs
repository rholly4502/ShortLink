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
    private readonly IClickRepository _clickRepository;

    public ShortLinkService(
        IShortLinkRepository repository,
        ISlugGenerator slugGenerator,
        IUrlValidator urlValidator,
        IClock clock,
        IClickRepository clickRepository)
    {
        _repository = repository;
        _slugGenerator = slugGenerator;
        _urlValidator = urlValidator;
        _clock = clock;
        _clickRepository = clickRepository;
    }

    public Task<ShortenedUrl> CreateAsync(
        Guid ownerId,
        string url,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
        => CreateAsync(ownerId, url, expiresAt, customSlug: null, ct);

    public async Task<ShortenedUrl> CreateAsync(
        Guid ownerId,
        string url,
        DateTimeOffset? expiresAt,
        string? customSlug,
        CancellationToken ct = default)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("OwnerId tidak valid.", nameof(ownerId));

        if (!_urlValidator.TryNormalize(url, out var normalized))
            throw new ArgumentException("URL tidak valid atau tidak aman.", nameof(url));

        if (expiresAt is { } expiry && expiry <= _clock.UtcNow)
            throw new ArgumentException("ExpiresAt harus berada di masa depan.", nameof(expiresAt));

        string slug;
        if (!string.IsNullOrEmpty(customSlug))
        {
            if (!SlugValidator.TryValidateCustomSlug(customSlug, out slug, out var slugError))
                throw new ArgumentException(slugError, nameof(customSlug));

            if (await _repository.SlugExistsAsync(slug, ct))
                throw new InvalidOperationException($"Custom slug '{slug}' sudah digunakan.");
        }
        else
        {
            do
            {
                slug = _slugGenerator.Generate();
            }
            while (SlugValidator.IsReserved(slug) || await _repository.SlugExistsAsync(slug, ct));
        }

        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Url = normalized.AbsoluteUri,
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
        => ownerId == Guid.Empty ? new List<ShortenedUrl>() : await _repository.GetByOwnerAsync(ownerId, ct);

    public async Task<ShortenedUrl?> GetForOwnerAsync(Guid ownerId, Guid id, CancellationToken ct = default)
    {
        if (ownerId == Guid.Empty)
            return null;

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
        if (ownerId == Guid.Empty)
            throw new UnauthorizedAccessException();

        if (!_urlValidator.TryNormalize(url, out var normalized))
            throw new ArgumentException("URL tidak valid atau tidak aman.", nameof(url));

        if (expiresAt is { } expiry && expiry <= _clock.UtcNow)
            throw new ArgumentException("ExpiresAt harus berada di masa depan.", nameof(expiresAt));

        var link = await _repository.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException("Short link tidak ditemukan.");
        if (link.OwnerId != ownerId)
            throw new UnauthorizedAccessException();

        link.Url = normalized.AbsoluteUri;
        link.ExpiresAt = expiresAt;
        await _repository.UpdateAsync(link, ct);
        return link;
    }

    public async Task DeleteByOwnerAsync(Guid ownerId, Guid id, CancellationToken ct = default)
    {
        if (ownerId == Guid.Empty)
            throw new UnauthorizedAccessException();

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
            .GroupBy(c => (c.ShortenedUrlId, Bucket(c.ClickedAt, period), c.Device, c.Country))
            .Select(g => new ClickAggregate
            {
                Id = Guid.NewGuid(),
                ShortenedUrlId = g.Key.Item1,
                PeriodStart = g.Key.Item2,
                Period = period,
                Device = g.Key.Item3,
                Country = g.Key.Item4,
                Count = g.LongCount()
            })
            .OrderBy(a => a.PeriodStart)
            .ThenBy(a => a.Device)
            .ToList();
    }

    /// <summary>Ringkasan analytics untuk satu link milik owner (null bila bukan owner / tak ditemukan).</summary>
    public async Task<AnalyticsSummary?> GetAnalyticsAsync(Guid ownerId, Guid linkId, CancellationToken ct = default)
    {
        if (ownerId == Guid.Empty)
            return null;

        var link = await _repository.GetByIdAsync(linkId, ct);
        if (link is null || link.OwnerId != ownerId)
            return null;

        var aggregates = await _clickRepository.GetByShortLinkIdAsync(linkId, ct);
        var daily = aggregates.Where(a => a.Period == AggregatePeriod.Daily).ToList();

        return new AnalyticsSummary(
            linkId,
            daily.Sum(a => a.Count),
            daily.GroupBy(a => a.Device ?? DeviceType.Other)
                .Select(g => new DeviceStat(g.Key.ToString(), g.Sum(a => a.Count)))
                .OrderByDescending(x => x.Count)
                .ToList(),
            daily.Where(a => a.Country is not null)
                .GroupBy(a => a.Country!)
                .Select(g => new CountryStat(g.Key, g.Sum(a => a.Count)))
                .OrderByDescending(x => x.Count)
                .ToList(),
            daily.GroupBy(a => a.PeriodStart)
                .Select(g => new DayStat(g.Key, g.Sum(a => a.Count)))
                .OrderBy(x => x.Day)
                .ToList());
    }

    private static DateTimeOffset Bucket(DateTimeOffset time, AggregatePeriod period)
        => period switch
        {
            AggregatePeriod.Hourly => new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, 0, 0, time.Offset),
            _ => new DateTimeOffset(time.Year, time.Month, time.Day, 0, 0, 0, time.Offset)
        };
}
