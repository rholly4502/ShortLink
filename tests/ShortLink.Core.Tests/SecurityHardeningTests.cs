using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using ShortLink.Api.Controllers;
using ShortLink.Api.Errors;
using ShortLink.Api.Models;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

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

    // --- Anti auth-bypass: identitas tanpa subjek valid → deny semua operasi owner (401 Unauthorized) ---

    [Fact]
    public async Task Get_InvalidIdentity_ReturnsUnauthorized()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Get(link.Id, default);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task Update_InvalidIdentity_ReturnsUnauthorized_AndDoesNotMutate()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Update(link.Id, new UpdateLinkRequest("https://evil.example.com", null), default);

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.DoesNotContain(link.Id, _repo.Updated);
    }

    [Fact]
    public async Task Delete_InvalidIdentity_ReturnsUnauthorized_AndDoesNotMutate()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Delete(link.Id, default);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.DoesNotContain(link.Id, _repo.Deleted);
    }

    [Fact]
    public async Task Analytics_InvalidIdentity_ReturnsUnauthorized()
    {
        var link = await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.Analytics(link.Id, default);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task List_InvalidIdentity_ReturnsUnauthorized()
    {
        await SeedLink(_owner);
        var ctrl = NewController();

        var result = await ctrl.List(default);

        Assert.IsType<UnauthorizedResult>(result.Result);
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

    [Fact]
    public async Task Create_InvalidIdentity_ReturnsUnauthorized_AndDoesNotPersist()
    {
        var noClaimsCtrl = NewController();
        var noClaimsResult = await noClaimsCtrl.Create(new CreateLinkRequest("https://example.com", null), default);
        Assert.IsType<UnauthorizedResult>(noClaimsResult.Result);

        var emptySubCtrl = NewController(new Claim("sub", Guid.Empty.ToString()));
        var emptySubResult = await emptySubCtrl.Create(new CreateLinkRequest("https://example.com", null), default);
        Assert.IsType<UnauthorizedResult>(emptySubResult.Result);

        Assert.Empty(_repo.ById);
    }

    [Fact]
    public async Task Service_EmptyOwnerId_CannotCreateOrAccessEmptyOwnedLinks()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateAsync(Guid.Empty, "https://example.com"));

        var emptyOwned = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "empty01",
            Url = "https://example.com",
            OwnerId = Guid.Empty,
            CreatedAt = _clock.UtcNow
        };
        await _repo.AddAsync(emptyOwned);

        Assert.Empty(await _service.ListForOwnerAsync(Guid.Empty));
        Assert.Null(await _service.GetForOwnerAsync(Guid.Empty, emptyOwned.Id));
        Assert.Null(await _service.GetAnalyticsAsync(Guid.Empty, emptyOwned.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.UpdateUrlAsync(Guid.Empty, emptyOwned.Id, "https://example.org", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.DeleteByOwnerAsync(Guid.Empty, emptyOwned.Id));
    }

    [Fact]
    public async Task EndToEnd_CreateUpdateAndRedirect_BlockSsrfAndOpenRedirectBypasses()
    {
        await using var harness = new IntegrationHarness();
        var alice = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            PasswordHash = "hash",
            Role = UserRole.User,
            CreatedAt = DateTimeOffset.UtcNow
        };
        harness.Users.Add(alice);
        var tokens = harness.Tokens.Generate(alice);

        using var validCreateReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/a%20b?x=%2F", null, "safe123"))
        };
        validCreateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var validCreateRes = await harness.Client.SendAsync(validCreateReq);
        Assert.Equal(HttpStatusCode.Created, validCreateRes.StatusCode);
        var created = await validCreateRes.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(created);
        Assert.Equal("https://example.com/a%20b?x=%2F", created!.Url);

        foreach (var unsafeUrl in new[] { "http://postgres:5432/", "http://[::127.0.0.1]/", "https://evil.com\\@example.com" })
        {
            using var createBadReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
            {
                Content = JsonContent.Create(new CreateLinkRequest(unsafeUrl, null))
            };
            createBadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var createBadRes = await harness.Client.SendAsync(createBadReq);
            Assert.Equal(HttpStatusCode.BadRequest, createBadRes.StatusCode);
            var createErr = await createBadRes.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(createErr);
            Assert.False(string.IsNullOrWhiteSpace(createErr!.Error));

            using var updateBadReq = new HttpRequestMessage(HttpMethod.Put, $"/api/links/{created.Id}")
            {
                Content = JsonContent.Create(new UpdateLinkRequest(unsafeUrl, null))
            };
            updateBadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var updateBadRes = await harness.Client.SendAsync(updateBadReq);
            Assert.Equal(HttpStatusCode.BadRequest, updateBadRes.StatusCode);
            var updateErr = await updateBadRes.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(updateErr);
            Assert.False(string.IsNullOrWhiteSpace(updateErr!.Error));
        }

        await harness.Links.AddAsync(new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "legacybad",
            Url = "http://169.254.169.254/latest/meta-data/",
            OwnerId = alice.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var unsafeRedirectRes = await harness.Client.GetAsync("/legacybad");
        Assert.Equal(HttpStatusCode.NotFound, unsafeRedirectRes.StatusCode);
        Assert.Empty(harness.ClicksPublisher.Published);

        var safeRedirectRes = await harness.Client.GetAsync("/safe123");
        Assert.Equal(HttpStatusCode.Found, safeRedirectRes.StatusCode);
        Assert.Equal("https://example.com/a%20b?x=%2F", safeRedirectRes.Headers.Location?.OriginalString);
        Assert.Single(harness.ClicksPublisher.Published);
    }

    private sealed class IntegrationHarness : IAsyncDisposable
    {
        private readonly string _webRoot;
        private readonly WebApplicationFactory<Program> _factory;

        public HttpClient Client { get; }
        public JwtTokenService Tokens { get; }
        public FakeUserRepository Users { get; } = new();
        public FakeRepo Links { get; } = new();
        public RecordingClickEventPublisher ClicksPublisher { get; } = new();

        public IntegrationHarness()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-sec-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webRoot);

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(_webRoot);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(new FakeRedisStringStore());

                        services.RemoveAll<IClickEventPublisher>();
                        services.AddSingleton<IClickEventPublisher>(ClicksPublisher);

                        services.RemoveAll<IUserRepository>();
                        services.AddSingleton<IUserRepository>(Users);

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(Links);

                        services.RemoveAll<IClickRepository>();
                        services.AddSingleton<IClickRepository>(new FakeClickRepository());
                    });
                });

            Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
            Tokens = new JwtTokenService(Options.Create(jwtOptions));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            if (Directory.Exists(_webRoot))
                Directory.Delete(_webRoot, recursive: true);
        }
    }

    private sealed class FakeUserRepository : IUserRepository
    {
        private readonly List<User> _users = new();

        public void Add(User user) => _users.Add(user);
        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_users.FirstOrDefault(u => u.Id == id));
        public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
            => Task.FromResult(_users.FirstOrDefault(u => u.Username == username));
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
            => Task.FromResult(_users.FirstOrDefault(u => u.Email == email));
        public Task<bool> ExistsAsync(string username, string email, CancellationToken ct = default)
            => Task.FromResult(_users.Any(u => u.Username == username || u.Email == email));
        public Task AddAsync(User user, CancellationToken ct = default)
        {
            _users.Add(user);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingClickEventPublisher : IClickEventPublisher
    {
        public List<ClickEvent> Published { get; } = new();

        public Task PublishAsync(ClickEvent clickEvent, CancellationToken ct = default)
        {
            Published.Add(clickEvent);
            return Task.CompletedTask;
        }
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
