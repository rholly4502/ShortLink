using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

public class ServiceExpiryTests
{
    private readonly FixedClock _clock = new();
    private readonly FakeRepo _repo = new();
    private readonly ShortLinkService _sut;

    public ServiceExpiryTests()
    {
        _sut = new ShortLinkService(_repo, new SequenceSlugGenerator(["abc1234"]), new UrlValidator(), _clock, new FakeClickRepository());
    }

    [Fact]
    public async Task CreateAsync_PastExpiry_ThrowsArgumentException()
    {
        var past = _clock.UtcNow.AddDays(-1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.CreateAsync(Guid.NewGuid(), "https://example.com", past));
        Assert.Empty(_repo.Added);
    }

    [Fact]
    public async Task UpdateUrlAsync_PastExpiry_ThrowsArgumentException()
    {
        var link = await _sut.CreateAsync(Guid.NewGuid(), "https://example.com");
        var past = _clock.UtcNow.AddDays(-1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.UpdateUrlAsync(link.OwnerId, link.Id, "https://example.org", past));
    }

    [Fact]
    public async Task CreateAsync_FutureExpiry_IsAccepted()
    {
        var future = _clock.UtcNow.AddDays(7);
        var link = await _sut.CreateAsync(Guid.NewGuid(), "https://example.com", future);
        Assert.Equal(future, link.ExpiresAt);
    }

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

    private sealed class FakeRepo : IShortLinkRepository
    {
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();
        public Dictionary<Guid, ShortenedUrl> ById { get; } = new();
        public List<ShortenedUrl> Added { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var l) ? l : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(ById.TryGetValue(id, out var l) ? l : null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(Added.Where(x => x.OwnerId == ownerId).ToList());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.ContainsKey(slug));

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

    private sealed class FakeClickRepository : IClickRepository
    {
        public Task AddAsync(Click click, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(new List<ClickAggregate>());
    }
}
