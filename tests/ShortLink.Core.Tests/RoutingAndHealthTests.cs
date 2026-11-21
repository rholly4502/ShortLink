using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShortLink.Api.Models;
using ShortLink.Api.Routing;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

public sealed class RoutingAndHealthTests
{
    // --- NonReservedSlugRouteConstraint unit tests ---

    [Theory]
    [InlineData("login")]
    [InlineData("LOGIN")]
    [InlineData("register")]
    [InlineData("links")]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData("auth")]
    [InlineData("shortlink")]
    [InlineData("health")]
    [InlineData("dashboard")]
    public void NonReservedSlugConstraint_ReservedSlugs_ReturnsFalse(string slug)
    {
        var constraint = new NonReservedSlugRouteConstraint();
        var values = new RouteValueDictionary { ["slug"] = slug };

        var matched = constraint.Match(
            httpContext: new DefaultHttpContext(),
            route: null,
            routeKey: "slug",
            values: values,
            routeDirection: RouteDirection.IncomingRequest);

        Assert.False(matched);
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("Promo2026")]
    [InlineData("mySlug99")]
    public void NonReservedSlugConstraint_NonReservedSlugs_ReturnsTrue(string slug)
    {
        var constraint = new NonReservedSlugRouteConstraint();
        var values = new RouteValueDictionary { ["slug"] = slug };

        var matched = constraint.Match(
            httpContext: new DefaultHttpContext(),
            route: null,
            routeKey: "slug",
            values: values,
            routeDirection: RouteDirection.IncomingRequest);

        Assert.True(matched);
    }

    [Fact]
    public void NonReservedSlugConstraint_MissingNullEmptyOrWhitespace_ReturnsFalse()
    {
        var constraint = new NonReservedSlugRouteConstraint();

        Assert.False(constraint.Match(null, null, "slug", new RouteValueDictionary(), RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "slug", new RouteValueDictionary { ["slug"] = null }, RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "slug", new RouteValueDictionary { ["slug"] = "" }, RouteDirection.IncomingRequest));
        Assert.False(constraint.Match(null, null, "slug", new RouteValueDictionary { ["slug"] = "   " }, RouteDirection.IncomingRequest));
    }

    // --- End-to-end integration tests: /api/health, SPA single-segment fallback, /api & /auth isolation ---

    [Fact]
    public async Task HealthEndpoint_GetAndHead_ReturnOkJsonWithoutAuth()
    {
        await using var harness = new TestHarness();

        var getRes = await harness.NoRedirectClient.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        Assert.Equal("application/json", getRes.Content.Headers.ContentType?.MediaType);

        var body = await getRes.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());

        using var headReq = new HttpRequestMessage(HttpMethod.Head, "/api/health");
        var headRes = await harness.NoRedirectClient.SendAsync(headReq);
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_WhenRedisUnavailable_StillReturnsOk()
    {
        await using var harness = new TestHarness(useFaultyRedis: true);

        var res = await harness.NoRedirectClient.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/links")]
    [InlineData("/dashboard")]
    [InlineData("/LOGIN")]
    public async Task SingleSegmentSpaRoutes_ReturnSpaIndexWithoutTouchingRedisOrRepository(string path)
    {
        await using var harness = new TestHarness();

        var res = await harness.NoRedirectClient.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("ShortLink SPA", body);
        Assert.Empty(harness.Links.SlugLookups);
        Assert.DoesNotContain(harness.Redis.ReadKeys, k => k.StartsWith("slug:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/API")]
    [InlineData("/api/")]
    [InlineData("/api/nonexistent")]
    [InlineData("/api/links/not-a-guid")]
    [InlineData("/auth")]
    [InlineData("/AUTH")]
    [InlineData("/auth/nonexistent")]
    [InlineData("/auth/login")]
    public async Task UnmatchedApiAndAuthRoutes_ReturnNotFoundWithoutSpaFallbackOrSlugLookup(string path)
    {
        await using var harness = new TestHarness();

        var res = await harness.NoRedirectClient.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ShortLink SPA", body);
        Assert.Empty(harness.Links.SlugLookups);
    }

    [Fact]
    public async Task ProtectedApiAndAuthEndpoints_ContinueWorkingWithRoutingChanges()
    {
        await using var harness = new TestHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var pair = harness.Tokens.Generate(user);

        var unauthRes = await harness.NoRedirectClient.GetAsync("/api/links");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        var listRes = await harness.NoRedirectClient.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/custom", null, "promo2026"))
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        var createRes = await harness.NoRedirectClient.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var refreshRes = await harness.NoRedirectClient.PostAsJsonAsync("/auth/refresh", new RefreshRequest(pair.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshRes.StatusCode);
        var refreshed = await refreshRes.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(refreshed);
        Assert.False(string.IsNullOrWhiteSpace(refreshed!.AccessToken));
    }

    [Fact]
    public async Task RedirectHotPath_ValidSlugRedirectsAndUnknownSlugReturns404WithoutSpaFallback()
    {
        await using var harness = new TestHarness();
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "promo2026",
            Url = "https://example.com/promo",
            OwnerId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await harness.Links.AddAsync(link);

        var redirectRes = await harness.NoRedirectClient.GetAsync("/promo2026");
        Assert.Equal(HttpStatusCode.Found, redirectRes.StatusCode);
        Assert.Equal("https://example.com/promo", redirectRes.Headers.Location?.ToString());
        var evt = Assert.Single(harness.ClickPublisher.Published);
        Assert.Equal(link.Id, evt.ShortenedUrlId);

        var missingRes = await harness.NoRedirectClient.GetAsync("/unknownslug999");
        Assert.Equal(HttpStatusCode.NotFound, missingRes.StatusCode);
        var missingBody = await missingRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ShortLink SPA", missingBody);
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

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _webRoot;
        private readonly WebApplicationFactory<Program> _factory;

        public HttpClient NoRedirectClient { get; }
        public JwtTokenService Tokens { get; }
        public RecordingRedisStringStore Redis { get; } = new();
        public RecordingClickEventPublisher ClickPublisher { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public RecordingShortLinkRepository Links { get; } = new();

        public TestHarness(bool useFaultyRedis = false)
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-routing-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webRoot);
            File.WriteAllText(
                Path.Combine(_webRoot, "index.html"),
                "<!DOCTYPE html><html><head><title>ShortLink</title></head><body>ShortLink SPA</body></html>");

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(_webRoot);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(
                            useFaultyRedis ? new FaultyRedisStringStore() : Redis);

                        services.RemoveAll<IClickEventPublisher>();
                        services.AddSingleton<IClickEventPublisher>(ClickPublisher);

                        services.RemoveAll<IUserRepository>();
                        services.AddSingleton<IUserRepository>(Users);

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(Links);

                        services.RemoveAll<IClickRepository>();
                        services.AddSingleton<IClickRepository>(new InMemoryClickRepository());
                    });
                });

            NoRedirectClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
            Tokens = new JwtTokenService(Options.Create(jwtOptions));
        }

        public async ValueTask DisposeAsync()
        {
            NoRedirectClient.Dispose();
            await _factory.DisposeAsync();
            if (Directory.Exists(_webRoot))
                Directory.Delete(_webRoot, recursive: true);
        }
    }

    private sealed class RecordingRedisStringStore : IRedisStringStore
    {
        private readonly FakeRedisStringStore _inner = new();
        public List<string> ReadKeys { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            ReadKeys.Add(key);
            return _inner.GetAsync(key, ct);
        }

        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
            => _inner.SetAsync(key, value, ttl, ct);

        public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
            => _inner.IncrementAsync(key, ttl, ct);

        public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
            => _inner.DeleteAsync(key, ct);
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

    private sealed class RecordingShortLinkRepository : IShortLinkRepository
    {
        public Dictionary<Guid, ShortenedUrl> ById { get; } = new();
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();
        public List<string> SlugLookups { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
        {
            SlugLookups.Add(slug);
            return Task.FromResult(BySlug.TryGetValue(slug, out var link) ? link : null);
        }

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(ById.TryGetValue(id, out var link) ? link : null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(ById.Values.Where(l => l.OwnerId == ownerId).ToList());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            ById[link.Id] = link;
            BySlug[link.Slug] = link;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            ById[link.Id] = link;
            BySlug[link.Slug] = link;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default)
        {
            ById.Remove(link.Id);
            BySlug.Remove(link.Slug);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryClickRepository : IClickRepository
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
