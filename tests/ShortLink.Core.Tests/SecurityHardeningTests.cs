using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Security.Claims;
using ShortLink.Api.Controllers;
using ShortLink.Api.Models;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

/// <summary>P5.1 — penguatan verifikasi keamanan: kepastian oworisasi endpoint & cegah auth-bypass via identitas rusak.</summary>
public sealed class SecurityHardeningTests
{
    private readonly FixedClock _clock = new();
    private readonly SequenceSlugGenerator _slugs = new(["abc1234", "xyz9999"]);
    private readonly FakeRepo _repo = new();
    private readonly ShortLinkService _service;
    private readonly Guid _owner = Guid.NewGuid();

    public SecurityHardeningTests()
    {
        _service = new ShortLinkService(_repo, _slugs, new UrlValidator(), _clock, new FakeClickRepository());
    }

    // --- Otorisasi endpoint: atribut [Authorize] ---

    [Fact]
    public void ShortLinksController_IsDecoratedWithAuthorize() =>
        Assert.NotNull(typeof(ShortLinksController).GetCustomAttribute<AuthorizeAttribute>());

    [Fact]
    public void RedirectController_IsPublic_NoAuthorize() =>
        Assert.Null(typeof(RedirectController).GetCustomAttribute<AuthorizeAttribute>());

    [Fact]
    public void AuthController_IsPublic_NoAuthorize() =>
        Assert.Null(typeof(AuthController).GetCustomAttribute<AuthorizeAttribute>());

    // --- Anti auth-bypass: identitas tanpa subjek valid → deny semua operasi owner ---

    [Fact]
    public async Task Get_InvalidIdentity_ReturnsNotFound()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Get(link.Id, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_InvalidIdentity_ReturnsNotFound_AndDoesNotMutate()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Update(link.Id, new UpdateLinkRequest("https://evil.example.com", null), default);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.DoesNotContain(link.Id, _repo.Updated);
    }

    [Fact]
    public async Task Delete_InvalidIdentity_ReturnsNotFound_AndDoesNotMutate()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Delete(link.Id, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.DoesNotContain(link.Id, _repo.Deleted);
    }

    [Fact]
    public async Task Analytics_InvalidIdentity_ReturnsNotFound()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Analytics(link.Id, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task List_InvalidIdentity_ReturnsEmpty()
    {
        await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.List(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var links = Assert.IsType<List<LinkResponse>>(ok.Value);
        Assert.Empty(links);
    }

    // --- Fallback klaim "sub" bekerja (bentuk hasil pemetaan inbound JWT) ---

    [Fact]
    public async Task Get_SubClaimOnly_ResolvesOwner_ReturnsOk()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController(new Claim("sub", _owner.ToString()));

        var result = await ctrl.Get(link.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(link.Id, Assert.IsType<LinkResponse>(ok.Value).Id);
    }

    private async Task<ShortenedUrl> SeedLink(Guid ownerId, string url = "https://example.com")
        => await _service.CreateAsync(ownerId, url);

    private ShortLinksController NewController(params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new ShortLinksController(_service, new FakeSlugCache())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
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

    private sealed class FakeClickRepository : IClickRepository
    {
        public Task AddAsync(Click click, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(new List<ClickAggregate>());
    }
}
