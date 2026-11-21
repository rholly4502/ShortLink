using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShortLink.Api.Controllers;
using ShortLink.Api.Errors;
using ShortLink.Api.Models;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

public sealed class CustomSlugTests
{
    private readonly FixedClock _clock = new();
    private readonly Guid _owner = Guid.NewGuid();

    // --- SlugValidator unit tests ---

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("Promo2026", "Promo2026")]
    [InlineData("  trimmed123  ", "trimmed123")]
    [InlineData("A1b2C3d4E5", "A1b2C3d4E5")]
    public void TryValidateCustomSlug_ValidSlugs_ReturnsNormalizedSlug(string input, string expected)
    {
        var ok = SlugValidator.TryValidateCustomSlug(input, out var normalized, out var error);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateCustomSlug_ExactMaxLength_SucceedsAndOverMaxLength_Fails()
    {
        var maxLenSlug = new string('a', SlugValidator.MaxLength);
        var tooLongSlug = new string('a', SlugValidator.MaxLength + 1);

        Assert.True(SlugValidator.TryValidateCustomSlug(maxLenSlug, out var normalized, out var okErr));
        Assert.Equal(maxLenSlug, normalized);
        Assert.Null(okErr);

        Assert.False(SlugValidator.TryValidateCustomSlug(tooLongSlug, out var rejected, out var failErr));
        Assert.Equal(string.Empty, rejected);
        Assert.False(string.IsNullOrWhiteSpace(failErr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("my-slug")]
    [InlineData("my_slug")]
    [InlineData("my slug")]
    [InlineData("api/v1")]
    [InlineData("a.b")]
    [InlineData("../etc")]
    [InlineData("café")]
    public void TryValidateCustomSlug_InvalidSlugs_ReturnsFalseAndError(string? input)
    {
        var ok = SlugValidator.TryValidateCustomSlug(input, out var normalized, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData("Api")]
    [InlineData("auth")]
    [InlineData("AUTH")]
    [InlineData("shortlink")]
    [InlineData("ShortLink")]
    [InlineData("login")]
    [InlineData("register")]
    [InlineData("links")]
    [InlineData("analytics")]
    [InlineData("assets")]
    [InlineData("openapi")]
    [InlineData("swagger")]
    [InlineData("health")]
    [InlineData("healthz")]
    [InlineData("admin")]
    [InlineData("favicon")]
    [InlineData("index")]
    [InlineData("robots")]
    [InlineData("sitemap")]
    public void TryValidateCustomSlug_ReservedPaths_RejectedCaseInsensitively(string reserved)
    {
        var ok = SlugValidator.TryValidateCustomSlug(reserved, out var normalized, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
        Assert.Contains("terreservasi", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("api", true)]
    [InlineData("API", true)]
    [InlineData("  login  ", true)]
    [InlineData("shortlink", true)]
    [InlineData("promo2026", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsReserved_HandlesReservedNonReservedAndBlank(string? slug, bool expected)
    {
        Assert.Equal(expected, SlugValidator.IsReserved(slug));
    }

    // --- ShortLinkService unit tests ---

    [Fact]
    public async Task CreateAsync_WithValidCustomSlug_UsesTrimmedSlugWithoutInvokingGenerator()
    {
        var repo = new InMemoryShortLinkRepository();
        var generator = new CountingSlugGenerator(["gen1234"]);
        var service = CreateService(repo, generator);

        var link = await service.CreateAsync(_owner, "https://example.com/offer", null, "  Promo2026  ");

        Assert.Equal("Promo2026", link.Slug);
        Assert.Equal("https://example.com/offer", link.Url);
        Assert.Equal(0, generator.Calls);
        Assert.Single(repo.Added);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreateAsync_WithNullOrEmptyCustomSlug_AutoGeneratesSlug(string? customSlug)
    {
        var repo = new InMemoryShortLinkRepository();
        var generator = new CountingSlugGenerator(["abc1234"]);
        var service = CreateService(repo, generator);

        var link = await service.CreateAsync(_owner, "https://example.com", null, customSlug);

        Assert.Equal("abc1234", link.Slug);
        Assert.Equal(1, generator.Calls);
        Assert.Single(repo.Added);
    }

    [Fact]
    public async Task CreateAsync_AutoGeneratedSlug_SkipsReservedPaths()
    {
        var repo = new InMemoryShortLinkRepository();
        var generator = new CountingSlugGenerator(["openapi", "LOGIN", "abc1234"]);
        var service = CreateService(repo, generator);

        var link = await service.CreateAsync(_owner, "https://example.com");

        Assert.Equal("abc1234", link.Slug);
        Assert.Equal(3, generator.Calls);
        Assert.Single(repo.Added);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("bad-slug")]
    [InlineData("login")]
    [InlineData("API")]
    [InlineData("shortlink")]
    public async Task CreateAsync_WithInvalidOrReservedCustomSlug_ThrowsArgumentExceptionAndDoesNotPersist(string customSlug)
    {
        var repo = new InMemoryShortLinkRepository();
        var service = CreateService(repo, new CountingSlugGenerator(["unused1"]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(_owner, "https://example.com", null, customSlug));
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task CreateAsync_WithTooLongCustomSlug_ThrowsArgumentExceptionAndDoesNotPersist()
    {
        var repo = new InMemoryShortLinkRepository();
        var service = CreateService(repo, new CountingSlugGenerator(["unused1"]));
        var tooLong = new string('x', SlugValidator.MaxLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(_owner, "https://example.com", null, tooLong));
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateCustomSlug_ThrowsInvalidOperationExceptionAndDoesNotPersist()
    {
        var repo = new InMemoryShortLinkRepository();
        var service = CreateService(repo, new CountingSlugGenerator(["unused1"]));
        await service.CreateAsync(_owner, "https://example.com/first", null, "promo2026");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(_owner, "https://example.org/second", null, "promo2026"));
        Assert.Single(repo.Added);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidUrlOrPastExpiryAndValidCustomSlug_ThrowsArgumentExceptionAndDoesNotPersist()
    {
        var repo = new InMemoryShortLinkRepository();
        var service = CreateService(repo, new CountingSlugGenerator(["unused1"]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(_owner, "javascript:alert(1)", null, "promo2026"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(_owner, "https://example.com", _clock.UtcNow.AddMinutes(-1), "promo2026"));
        Assert.Empty(repo.Added);
    }

    // --- ShortLinksController unit tests ---

    [Fact]
    public async Task Controller_Create_WithValidCustomSlug_ReturnsCreatedWithMatchingSlug()
    {
        var repo = new InMemoryShortLinkRepository();
        var ctrl = NewController(_owner, repo);

        var result = await ctrl.Create(new CreateLinkRequest("https://example.com/sale", null, "sale2026"), default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<LinkResponse>(created.Value);
        Assert.Equal("sale2026", body.Slug);
    }

    [Theory]
    [InlineData("bad-slug")]
    [InlineData("login")]
    [InlineData("API")]
    public async Task Controller_Create_WithReservedOrInvalidCustomSlug_ReturnsBadRequestWithApiError(string customSlug)
    {
        var repo = new InMemoryShortLinkRepository();
        var ctrl = NewController(_owner, repo);

        var result = await ctrl.Create(new CreateLinkRequest("https://example.com/sale", null, customSlug), default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiError = Assert.IsType<ApiError>(badRequest.Value);
        Assert.False(string.IsNullOrWhiteSpace(apiError.Error));
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task Controller_Create_WithDuplicateCustomSlug_ReturnsConflictWithApiError()
    {
        var repo = new InMemoryShortLinkRepository();
        var ctrl = NewController(_owner, repo);
        await ctrl.Create(new CreateLinkRequest("https://example.com/one", null, "dup2026"), default);

        var result = await ctrl.Create(new CreateLinkRequest("https://example.com/two", null, "dup2026"), default);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var apiError = Assert.IsType<ApiError>(conflict.Value);
        Assert.False(string.IsNullOrWhiteSpace(apiError.Error));
        Assert.Single(repo.Added);
    }

    [Fact]
    public async Task Controller_Create_WithInvalidIdentityAndCustomSlug_ReturnsUnauthorizedAndDoesNotPersist()
    {
        var repo = new InMemoryShortLinkRepository();
        var ctrl = NewController(Guid.Empty, repo, includeClaims: false);

        var result = await ctrl.Create(new CreateLinkRequest("https://example.com", null, "promo2026"), default);

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(repo.Added);
    }

    // --- End-to-end integration tests ---

    [Fact]
    public async Task EndToEnd_CreateCustomSlug_RedirectsToTargetUrl()
    {
        await using var harness = new TestHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var tokens = harness.Tokens.Generate(user);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/promo", null, "promo2026"))
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var createRes = await harness.Client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(created);
        Assert.Equal("promo2026", created!.Slug);

        var redirectRes = await harness.NoRedirectClient.GetAsync("/promo2026");
        Assert.Equal(HttpStatusCode.Found, redirectRes.StatusCode);
        Assert.Equal("https://example.com/promo", redirectRes.Headers.Location?.ToString());
    }

    [Fact]
    public async Task EndToEnd_CreateDuplicateCustomSlug_ReturnsConflict()
    {
        await using var harness = new TestHarness();
        var alice = CreateUser("alice", "alice@example.com");
        var bob = CreateUser("bob", "bob@example.com");
        harness.Users.Add(alice);
        harness.Users.Add(bob);
        var aliceTokens = harness.Tokens.Generate(alice);
        var bobTokens = harness.Tokens.Generate(bob);

        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/one", null, "promo2026"))
        };
        firstReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.AccessToken);
        var firstRes = await harness.Client.SendAsync(firstReq);
        Assert.Equal(HttpStatusCode.Created, firstRes.StatusCode);

        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/two", null, "promo2026"))
        };
        secondReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        var secondRes = await harness.Client.SendAsync(secondReq);

        Assert.Equal(HttpStatusCode.Conflict, secondRes.StatusCode);
        var error = await secondRes.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("API")]
    [InlineData("shortlink")]
    [InlineData("links")]
    public async Task EndToEnd_CreateReservedSlug_ReturnsBadRequestAndPreservesSpaAndApiRoutes(string reserved)
    {
        await using var harness = new TestHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var tokens = harness.Tokens.Generate(user);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/nope", null, reserved))
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var createRes = await harness.Client.SendAsync(createReq);

        Assert.Equal(HttpStatusCode.BadRequest, createRes.StatusCode);
        var error = await createRes.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));
        Assert.Empty(harness.Links.ById);

        var spaFallbackRes = await harness.Client.GetAsync("/dashboard/some/deep/route");
        Assert.Equal(HttpStatusCode.OK, spaFallbackRes.StatusCode);
        Assert.Contains("ShortLink SPA", await spaFallbackRes.Content.ReadAsStringAsync());

        var reservedPathRes = await harness.NoRedirectClient.GetAsync($"/{reserved}");
        Assert.NotEqual(HttpStatusCode.Found, reservedPathRes.StatusCode);

        var unauthApiRes = await harness.Client.GetAsync("/api/links");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthApiRes.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_CreateInvalidCustomSlug_ReturnsBadRequest()
    {
        await using var harness = new TestHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var tokens = harness.Tokens.Generate(user);

        foreach (var invalid in new[] { "bad-slug", new string('z', SlugValidator.MaxLength + 1) })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/links")
            {
                Content = JsonContent.Create(new CreateLinkRequest("https://example.com", null, invalid))
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var res = await harness.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        Assert.Empty(harness.Links.ById);
    }

    // --- Helpers & fakes ---

    private ShortLinkService CreateService(IShortLinkRepository repo, ISlugGenerator generator)
        => new(repo, generator, new UrlValidator(), _clock, new FakeClickRepository());

    private ShortLinksController NewController(Guid caller, IShortLinkRepository repo, bool includeClaims = true)
    {
        var service = CreateService(repo, new CountingSlugGenerator(["gen1234"]));
        var claims = includeClaims
            ? new[] { new Claim(ClaimTypes.NameIdentifier, caller.ToString()) }
            : Array.Empty<Claim>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new ShortLinksController(service, new FakeSlugCache())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    private static User CreateUser(string username, string email) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = email,
        PasswordHash = "hash",
        Role = UserRole.User,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class CountingSlugGenerator(IEnumerable<string> slugs) : ISlugGenerator
    {
        private readonly Queue<string> _slugs = new(slugs);
        public int Calls { get; private set; }

        public string Generate(int length = 7)
        {
            Calls++;
            return _slugs.Dequeue();
        }
    }

    private sealed class InMemoryShortLinkRepository : IShortLinkRepository
    {
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();
        public Dictionary<Guid, ShortenedUrl> ById { get; } = new();
        public List<ShortenedUrl> Added { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var l) ? l : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(ById.TryGetValue(id, out var l) ? l : null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(ById.Values.Where(l => l.OwnerId == ownerId).ToList());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            Added.Add(link);
            BySlug[link.Slug] = link;
            ById[link.Id] = link;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            BySlug[link.Slug] = link;
            ById[link.Id] = link;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            BySlug.Remove(link.Slug);
            ById.Remove(link.Id);
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

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _webRoot;
        private readonly WebApplicationFactory<Program> _factory;

        public HttpClient Client { get; }
        public HttpClient NoRedirectClient { get; }
        public JwtTokenService Tokens { get; }
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryShortLinkRepository Links { get; } = new();

        public TestHarness()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-custom-slug-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webRoot);
            File.WriteAllText(
                Path.Combine(_webRoot, "index.html"),
                "<!DOCTYPE html><html><body>ShortLink SPA</body></html>");

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
                        services.AddSingleton<IClickEventPublisher>(new NoOpClickEventPublisher());

                        services.RemoveAll<IUserRepository>();
                        services.AddSingleton<IUserRepository>(Users);

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(Links);

                        services.RemoveAll<IClickRepository>();
                        services.AddSingleton<IClickRepository>(new FakeClickRepository());
                    });
                });

            Client = _factory.CreateClient();
            NoRedirectClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
            Tokens = new JwtTokenService(Options.Create(jwtOptions));
        }

        public async ValueTask DisposeAsync()
        {
            NoRedirectClient.Dispose();
            Client.Dispose();
            await _factory.DisposeAsync();
            if (Directory.Exists(_webRoot))
                Directory.Delete(_webRoot, recursive: true);
        }
    }

    private sealed class NoOpClickEventPublisher : IClickEventPublisher
    {
        public Task PublishAsync(ClickEvent clickEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryUserRepository : IUserRepository
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
}
