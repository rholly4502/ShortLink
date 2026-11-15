using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Api.Controllers;
using ShortLink.Api.Models;

namespace ShortLink.Core.Tests;

public class ShortLinkApiTests
{
    private readonly FixedClock _clock = new();
    private readonly SequenceSlugGenerator _slugs = new(["abc1234", "xyz9999", "def4567"]);
    private readonly FakeRepo _repo = new();
    private readonly ShortLinkService _service;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly FakeClickRepository _clickRepo = new();

    public ShortLinkApiTests()
    {
        _service = new ShortLinkService(_repo, _slugs, new UrlValidator(), _clock, _clickRepo);
    }

    private async Task<ShortenedUrl> SeedLink(Guid ownerId, string url = "https://example.com")
    {
        var link = await _service.CreateAsync(ownerId, url);
        return link;
    }

    private ShortLinksController NewController(Guid caller)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, caller.ToString()) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new ShortLinksController(_service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    // --- Service: update ---

    [Fact]
    public async Task UpdateUrl_AsOwner_UpdatesUrlAndExpiry()
    {
        var link = await SeedLink(_owner);
        var expires = _clock.UtcNow.AddDays(7);

        var updated = await _service.UpdateUrlAsync(_owner, link.Id, "https://example.org/new", expires);

        Assert.Equal("https://example.org/new", updated.Url);
        Assert.Equal(expires, updated.ExpiresAt);
        Assert.Contains(link.Id, _repo.Updated);
    }

    [Fact]
    public async Task UpdateUrl_InvalidUrl_ThrowsArgumentException()
    {
        var link = await SeedLink(_owner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UpdateUrlAsync(_owner, link.Id, "javascript:alert(1)", null));
        Assert.DoesNotContain(link.Id, _repo.Updated);
    }

    [Fact]
    public async Task UpdateUrl_AsNonOwner_ThrowsUnauthorizedAccessException()
    {
        var link = await SeedLink(_owner);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.UpdateUrlAsync(_other, link.Id, "https://example.org", null));
        Assert.DoesNotContain(link.Id, _repo.Updated);
    }

    [Fact]
    public async Task UpdateUrl_UnknownId_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.UpdateUrlAsync(_owner, Guid.NewGuid(), "https://example.org", null));
    }

    // --- Service: delete ---

    [Fact]
    public async Task DeleteByOwner_AsOwner_RemovesLink()
    {
        var link = await SeedLink(_owner);

        await _service.DeleteByOwnerAsync(_owner, link.Id);

        Assert.Contains(link.Id, _repo.Deleted);
    }

    [Fact]
    public async Task DeleteByOwner_AsNonOwner_ThrowsUnauthorizedAccessException()
    {
        var link = await SeedLink(_owner);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.DeleteByOwnerAsync(_other, link.Id));
        Assert.DoesNotContain(link.Id, _repo.Deleted);
    }

    [Fact]
    public async Task DeleteByOwner_UnknownId_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.DeleteByOwnerAsync(_owner, Guid.NewGuid()));
    }

    // --- Service: get / list ---

    [Fact]
    public async Task GetForOwner_AsOwner_ReturnsLink()
    {
        var link = await SeedLink(_owner);
        var found = await _service.GetForOwnerAsync(_owner, link.Id);
        Assert.NotNull(found);
    }

    [Fact]
    public async Task GetForOwner_AsNonOwner_ReturnsNull()
    {
        var link = await SeedLink(_owner);
        var found = await _service.GetForOwnerAsync(_other, link.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task ListForOwner_ReturnsOnlyOwnLinks()
    {
        await SeedLink(_owner);
        await SeedLink(_other);
        var listed = await _service.ListForOwnerAsync(_owner);
        Assert.Single(listed);
        Assert.All(listed, l => Assert.Equal(_owner, l.OwnerId));
    }

    // --- Controller: create ---

    [Fact]
    public async Task Create_ValidRequest_ReturnsCreatedWithLink()
    {
        var ctrl = NewController(_owner);
        var result = await ctrl.Create(new CreateLinkRequest("https://example.com", null), default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        var body = Assert.IsType<LinkResponse>(created.Value);
        Assert.Equal("abc1234", body.Slug);
        Assert.Equal("https://example.com/", body.Url);
    }

    [Fact]
    public async Task Create_InvalidUrl_ReturnsBadRequest()
    {
        var ctrl = NewController(_owner);
        var result = await ctrl.Create(new CreateLinkRequest("javascript:alert(1)", null), default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // --- Controller: list ---

    [Fact]
    public async Task List_ReturnsOwnLinks()
    {
        await SeedLink(_owner);
        await SeedLink(_other);
        var ctrl = NewController(_owner);

        var result = await ctrl.List(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var links = Assert.IsType<List<LinkResponse>>(ok.Value);
        Assert.Single(links);
        Assert.All(links, l => Assert.Equal(_owner, l.OwnerId));
    }

    // --- Controller: get ---

    [Fact]
    public async Task Get_AsOwner_ReturnsOk()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_owner);

        var result = await ctrl.Get(link.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<LinkResponse>(ok.Value);
        Assert.Equal(link.Id, body.Id);
    }

    [Fact]
    public async Task Get_AsNonOwner_ReturnsNotFound()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_other);

        var result = await ctrl.Get(link.Id, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // --- Controller: update ---

    [Fact]
    public async Task Update_AsOwner_ReturnsOkUpdated()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_owner);

        var result = await ctrl.Update(link.Id, new UpdateLinkRequest("https://example.org", null), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<LinkResponse>(ok.Value);
        Assert.Equal("https://example.org/", body.Url);
    }

    [Fact]
    public async Task Update_AsNonOwner_ReturnsNotFound()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_other);

        var result = await ctrl.Update(link.Id, new UpdateLinkRequest("https://example.org", null), default);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.DoesNotContain(link.Id, _repo.Updated);
    }

    [Fact]
    public async Task Update_InvalidUrl_ReturnsBadRequest()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_owner);

        var result = await ctrl.Update(link.Id, new UpdateLinkRequest("javascript:alert(1)", null), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.DoesNotContain(link.Id, _repo.Updated);
    }

    // --- Controller: delete ---

    [Fact]
    public async Task Delete_AsOwner_ReturnsNoContent()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_owner);

        var result = await ctrl.Delete(link.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains(link.Id, _repo.Deleted);
    }

    [Fact]
    public async Task Delete_AsNonOwner_ReturnsNotFound()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_other);

        var result = await ctrl.Delete(link.Id, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.DoesNotContain(link.Id, _repo.Deleted);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        var ctrl = NewController(_owner);
        var result = await ctrl.Delete(Guid.NewGuid(), default);
        Assert.IsType<NotFoundResult>(result);
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

    private sealed class FakeRepo : IShortLinkRepository
    {
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();
        public Dictionary<Guid, ShortenedUrl> ById { get; } = new();
        public HashSet<string> SlugExists { get; } = new();
        public List<Guid> Updated { get; } = new();
        public List<Guid> Deleted { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var l) ? l : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(ById.TryGetValue(id, out var l) ? l : null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(ById.Values.Where(l => l.OwnerId == ownerId).ToList());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(SlugExists.Contains(slug) || BySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            BySlug[link.Slug] = link;
            ById[link.Id] = link;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            Updated.Add(link.Id);
            ById[link.Id] = link;
            BySlug[link.Slug] = link;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            Deleted.Add(link.Id);
            ById.Remove(link.Id);
            BySlug.Remove(link.Slug);
            return Task.CompletedTask;
        }
    }

    // --- Controller: analytics ---

    [Fact]
    public async Task Analytics_AsOwner_ReturnsOkWithSummary()
    {
        var link = await SeedLink(_owner);
        _clickRepo.Aggregates.Add(new ClickAggregate
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = link.Id,
            PeriodStart = _clock.UtcNow.Date,
            Period = AggregatePeriod.Daily,
            Device = DeviceType.Desktop,
            Country = "ID",
            Count = 5
        });
        var ctrl = NewController(_owner);

        var result = await ctrl.Analytics(link.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<AnalyticsSummary>(ok.Value);
        Assert.Equal(link.Id, body.LinkId);
        Assert.Equal(5L, body.TotalClicks);
    }

    [Fact]
    public async Task Analytics_AsNonOwner_ReturnsNotFound()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(_other);

        var result = await ctrl.Analytics(link.Id, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Analytics_UnknownLink_ReturnsNotFound()
    {
        var ctrl = NewController(_owner);
        var result = await ctrl.Analytics(Guid.NewGuid(), default);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    private sealed class FakeClickRepository : IClickRepository
    {
        public List<ClickAggregate> Aggregates { get; } = new();

        public Task AddAsync(Click click, CancellationToken ct = default) => Task.CompletedTask;

        public Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default) => Task.CompletedTask;

        public Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(Aggregates.Where(a => a.ShortenedUrlId == shortenedUrlId).ToList());
    }
}
