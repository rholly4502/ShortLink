using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

public class ShortLinkServiceTests
{
    private readonly FixedClock _clock = new();
    private readonly SequenceSlugGenerator _slugs;
    private readonly FakeShortLinkRepository _repo = new();
    private readonly FakeClickRepository _clickRepo = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly ShortLinkService _sut;

    public ShortLinkServiceTests()
    {
        _slugs = new SequenceSlugGenerator(["abc1234", "xyz9999"]);
        _sut = new ShortLinkService(_repo, _slugs, new UrlValidator(), _clock, _clickRepo);
    }

    // --- Create ---

    [Fact]
    public async Task Create_WithValidUrl_CreatesLinkWithGeneratedSlugAndOwner()
    {
        var link = await _sut.CreateAsync(Guid.NewGuid(), "https://example.com/page");

        Assert.NotNull(link);
        Assert.Equal("abc1234", link.Slug);
        Assert.Equal("https://example.com/page", link.Url);
        Assert.Equal(_clock.UtcNow, link.CreatedAt);
        Assert.Single(_repo.Added);
    }

    [Fact]
    public async Task Create_WhenSlugCollides_RegeneratesUntilUnique()
    {
        _repo.SlugExists.Add("abc1234"); // first generated slug collides
        var ownerId = Guid.NewGuid();

        var link = await _sut.CreateAsync(ownerId, "https://example.com");

        Assert.Equal("xyz9999", link.Slug);
        Assert.Single(_repo.Added);
    }

    [Fact]
    public async Task Create_WithInvalidUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(Guid.NewGuid(), "javascript:alert(1)"));
        Assert.Empty(_repo.Added);
    }

    [Fact]
    public async Task Create_WithUnsafeUrlUserInfo_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(Guid.NewGuid(), "https://evil@example.com"));
        Assert.Empty(_repo.Added);
    }

    [Fact]
    public async Task Create_WithExpiry_SetsExpiresAt()
    {
        var expires = _clock.UtcNow.AddDays(30);
        var link = await _sut.CreateAsync(Guid.NewGuid(), "https://example.com", expires);

        Assert.Equal(expires, link.ExpiresAt);
    }

    // --- Resolve ---

    [Fact]
    public async Task Resolve_ExistingActiveSlug_ReturnsLink()
    {
        var link = await _sut.CreateAsync(Guid.NewGuid(), "https://example.com");
        _repo.ById[link.Id] = link;
        _repo.BySlug[link.Slug] = link;

        var resolved = await _sut.ResolveAsync(link.Slug);

        Assert.NotNull(resolved);
        Assert.Equal(link.Id, resolved!.Id);
    }

    [Fact]
    public async Task Resolve_UnknownSlug_ReturnsNull()
    {
        var resolved = await _sut.ResolveAsync("nope123");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Resolve_ExpiredSlug_ReturnsNull()
    {
        // seed slug kedaluwarsa langsung via repo (CreateAsync menolak expiry lampau)
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "expired1",
            Url = "https://example.com",
            OwnerId = Guid.NewGuid(),
            CreatedAt = _clock.UtcNow.AddDays(-2),
            ExpiresAt = _clock.UtcNow.AddHours(-1)
        };
        _repo.ById[link.Id] = link;
        _repo.BySlug[link.Slug] = link;

        var resolved = await _sut.ResolveAsync(link.Slug);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolve_BlankSlug_ReturnsNull(string? slug)
    {
        var resolved = await _sut.ResolveAsync(slug!);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Resolve_PendingExpiry_ReturnsLink()
    {
        var link = await _sut.CreateAsync(Guid.NewGuid(), "https://example.com", _clock.UtcNow.AddHours(1));
        _repo.ById[link.Id] = link;
        _repo.BySlug[link.Slug] = link;

        var resolved = await _sut.ResolveAsync(link.Slug);

        Assert.NotNull(resolved);
    }

    // --- Analytics (pure aggregate) ---

    private static Click MakeClick(DateTimeOffset at, DeviceType device, string? country) => new()
    {
        Id = Guid.NewGuid(),
        ClickedAt = at,
        Device = device,
        Country = country
    };

    [Fact]
    public void AggregateClicks_Daily_GroupsByDayDeviceCountryAndCounts()
    {
        var day1 = new DateTimeOffset(2026, 11, 13, 10, 30, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 11, 14, 9, 15, 0, TimeSpan.Zero);
        var clicks = new[]
        {
            MakeClick(day1, DeviceType.Desktop, "ID"),
            MakeClick(day1, DeviceType.Desktop, "ID"),
            MakeClick(day1, DeviceType.Mobile, "ID"),
            MakeClick(day2, DeviceType.Desktop, "US"),
        };

        var aggregates = ShortLinkService.AggregateClicks(clicks, AggregatePeriod.Daily);

        Assert.Equal(3, aggregates.Count);
        var idDesktop = aggregates.Single(a => a.PeriodStart == day1.Date && a.Device == DeviceType.Desktop && a.Country == "ID");
        Assert.Equal(2L, idDesktop.Count);
        var idMobile = aggregates.Single(a => a.PeriodStart == day1.Date && a.Device == DeviceType.Mobile);
        Assert.Equal(1L, idMobile.Count);
        var usDesktop = aggregates.Single(a => a.PeriodStart == day2.Date && a.Country == "US");
        Assert.Equal(1L, usDesktop.Count);
    }

    [Fact]
    public void AggregateClicks_Hourly_GroupsByHour()
    {
        var clicks = new[]
        {
            MakeClick(new DateTimeOffset(2026, 11, 14, 10, 10, 0, TimeSpan.Zero), DeviceType.Desktop, null),
            MakeClick(new DateTimeOffset(2026, 11, 14, 10, 50, 0, TimeSpan.Zero), DeviceType.Desktop, null),
            MakeClick(new DateTimeOffset(2026, 11, 14, 11, 5, 0, TimeSpan.Zero), DeviceType.Desktop, null),
        };

        var aggregates = ShortLinkService.AggregateClicks(clicks, AggregatePeriod.Hourly);

        Assert.Equal(2, aggregates.Count);
        Assert.Equal(2L, aggregates.Single(a => a.PeriodStart.Hour == 10).Count);
        Assert.Equal(1L, aggregates.Single(a => a.PeriodStart.Hour == 11).Count);
    }

    [Fact]
    public void AggregateClicks_WithDefaultClickedAt_IsSkipped()
    {
        var clicks = new[]
        {
            MakeClick(default, DeviceType.Desktop, "ID"),
            MakeClick(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero), DeviceType.Desktop, "ID"),
        };

        var aggregates = ShortLinkService.AggregateClicks(clicks, AggregatePeriod.Daily);

        Assert.Single(aggregates);
        Assert.Equal(1L, aggregates[0].Count);
    }

    [Fact]
    public void AggregateClicks_NullClicks_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ShortLinkService.AggregateClicks(null!, AggregatePeriod.Daily));
    }

    [Fact]
    public void AggregateClicks_EmptyClicks_ReturnsEmptyList()
    {
        var aggregates = ShortLinkService.AggregateClicks(Array.Empty<Click>(), AggregatePeriod.Daily);

        Assert.Empty(aggregates);
    }

    // --- Fakes ---

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequenceSlugGenerator : ISlugGenerator
    {
        private readonly Queue<string> _slugs;

        public SequenceSlugGenerator(IEnumerable<string> slugs) => _slugs = new Queue<string>(slugs);

        public string Generate(int length = 7) => _slugs.Dequeue();
    }

    private sealed class FakeShortLinkRepository : IShortLinkRepository
    {
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();
        public Dictionary<Guid, ShortenedUrl> ById { get; } = new();
        public List<ShortenedUrl> Added { get; } = new();
        public HashSet<string> SlugExists { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var l) ? l : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(ById.TryGetValue(id, out var l) ? l : null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(Added.Where(x => x.OwnerId == ownerId).ToList());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(SlugExists.Contains(slug) || BySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            Added.Add(link);
            BySlug[link.Slug] = link;
            ById[link.Id] = link;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
    }

    // --- Analytics (service) ---

    private static ClickAggregate MakeAggregate(Guid linkId, DateTimeOffset day, DeviceType device, string? country, long count) => new()
    {
        Id = Guid.NewGuid(),
        ShortenedUrlId = linkId,
        PeriodStart = day.Date,
        Period = AggregatePeriod.Daily,
        Device = device,
        Country = country,
        Count = count
    };

    [Fact]
    public async Task GetAnalytics_AsOwner_ReturnsSummaryFromDailyAggregates()
    {
        var link = await _sut.CreateAsync(_owner, "https://example.com");
        _repo.ById[link.Id] = link;
        _repo.BySlug[link.Slug] = link;

        var day1 = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        _clickRepo.Aggregates.Add(MakeAggregate(link.Id, day1, DeviceType.Desktop, "ID", 2));
        _clickRepo.Aggregates.Add(MakeAggregate(link.Id, day1, DeviceType.Mobile, "ID", 1));
        _clickRepo.Aggregates.Add(MakeAggregate(link.Id, day2, DeviceType.Desktop, "US", 1));
        // agregasi hourly tidak boleh dihitung dua kali (hanya Daily dipakai)
        _clickRepo.Aggregates.Add(new ClickAggregate
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = link.Id,
            PeriodStart = day1.AddHours(9),
            Period = AggregatePeriod.Hourly,
            Device = DeviceType.Desktop,
            Country = "ID",
            Count = 2
        });

        var summary = await _sut.GetAnalyticsAsync(_owner, link.Id);

        Assert.NotNull(summary);
        Assert.Equal(4L, summary!.TotalClicks);
        var desktop = Assert.Single(summary.ByDevice, d => d.Device == "Desktop");
        Assert.Equal(3L, desktop.Count);
        var idCountry = Assert.Single(summary.ByCountry, c => c.Country == "ID");
        Assert.Equal(3L, idCountry.Count);
        Assert.Equal(2, summary.ByDay.Count);
        Assert.Equal(3L, summary.ByDay.Single(d => d.Day == day1.Date).Count);
    }

    [Fact]
    public async Task GetAnalytics_NonOwner_ReturnsNull()
    {
        var link = await _sut.CreateAsync(_owner, "https://example.com");
        _repo.ById[link.Id] = link;
        _repo.BySlug[link.Slug] = link;

        Assert.Null(await _sut.GetAnalyticsAsync(Guid.NewGuid(), link.Id));
    }

    [Fact]
    public async Task GetAnalytics_UnknownLink_ReturnsNull()
    {
        Assert.Null(await _sut.GetAnalyticsAsync(_owner, Guid.NewGuid()));
    }

    private sealed class FakeClickRepository : IClickRepository
    {
        public List<ClickAggregate> Aggregates { get; } = new();

        public Task AddAsync(Click click, CancellationToken ct = default) => Task.CompletedTask;

        public Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default) => Task.CompletedTask;

        public Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(Aggregates.Where(a => a.ShortenedUrlId == shortenedUrlId).ToList());
    }
}
