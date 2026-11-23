using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShortLink.Api.Errors;
using ShortLink.Api.Models;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

/// <summary>Integration tests end-to-end untuk rate limiting per scope, pengecualian /api/health, dan hardening X-Forwarded-For.</summary>
public sealed class RateLimitIntegrationTests
{
    [Fact]
    public async Task PostApiLinks_ExceedingCreateLimit_Returns429WithJsonApiError_WhileGetLinksAndHealthSucceed()
    {
        await using var harness = new TestHarness();
        var alice = CreateUser("alice", "alice@example.com");
        harness.Users.Add(alice);
        var tokens = harness.Tokens.Generate(alice);

        for (var i = 0; i < 2; i++)
        {
            using var okReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
            {
                Content = JsonContent.Create(new CreateLinkRequest($"https://example.com/{i}", null))
            };
            okReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            okReq.Headers.Add("X-Forwarded-For", "198.51.100.10");

            var okRes = await harness.Client.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.Created, okRes.StatusCode);
        }

        using var blockedReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/blocked", null))
        };
        blockedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        blockedReq.Headers.Add("X-Forwarded-For", "198.51.100.10");

        var blockedRes = await harness.Client.SendAsync(blockedReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedRes.StatusCode);
        Assert.Equal("60", blockedRes.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
        Assert.Equal("application/json", blockedRes.Content.Headers.ContentType?.MediaType);

        var rawBody = await blockedRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(rawBody))
        {
            Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
            Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));
        }

        var apiError = JsonSerializer.Deserialize<ApiError>(rawBody, JsonSerializerOptions.Web);
        Assert.NotNull(apiError);
        Assert.False(string.IsNullOrWhiteSpace(apiError!.Error));
        Assert.Equal(2, harness.Links.ById.Count);

        // API read bucket remains isolated from exhausted create bucket.
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        listReq.Headers.Add("X-Forwarded-For", "198.51.100.10");

        var listRes = await harness.Client.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        // Health probes bypass rate limiting and do not add Redis increments.
        var incrementsBeforeHealth = harness.Redis.Increments.Count;
        using var healthGetReq = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        healthGetReq.Headers.Add("X-Forwarded-For", "198.51.100.10");
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.SendAsync(healthGetReq)).StatusCode);

        using var healthHeadReq = new HttpRequestMessage(HttpMethod.Head, "/api/health");
        healthHeadReq.Headers.Add("X-Forwarded-For", "198.51.100.10");
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.SendAsync(healthHeadReq)).StatusCode);

        Assert.Equal(incrementsBeforeHealth, harness.Redis.Increments.Count);
    }

    [Fact]
    public async Task XForwardedFor_CanonicalizationAndMalformedFallback_EnforcedEndToEnd()
    {
        await using var harness = new TestHarness();

        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "wrong-pass"))
        };
        firstReq.Headers.Add("X-Forwarded-For", "::ffff:203.0.113.50");
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(firstReq)).StatusCode);

        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "wrong-pass"))
        };
        secondReq.Headers.Add("X-Forwarded-For", "203.0.113.50");
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(secondReq)).StatusCode);

        using var thirdReq = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "wrong-pass"))
        };
        thirdReq.Headers.Add("X-Forwarded-For", "198.51.100.99, 203.0.113.50");
        var thirdRes = await harness.Client.SendAsync(thirdReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, thirdRes.StatusCode);

        // Client attempting to rotate leftmost X-Forwarded-For IP while proxy appends real IP is still blocked.
        using var spoofedReq = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "wrong-pass"))
        };
        spoofedReq.Headers.Add("X-Forwarded-For", "198.51.100.123, 203.0.113.50");
        var spoofedRes = await harness.Client.SendAsync(spoofedReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofedRes.StatusCode);

        using var malformedReq = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("nobody", "wrong-pass"))
        };
        malformedReq.Headers.Add("X-Forwarded-For", "api:injected");
        var malformedRes = await harness.Client.SendAsync(malformedReq);
        Assert.Equal(HttpStatusCode.Unauthorized, malformedRes.StatusCode);

        Assert.DoesNotContain(harness.Redis.Counts.Keys, k => k.Contains("injected", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Redis.Counts.Keys, k => k.Contains("198.51.100.", StringComparison.Ordinal));
        Assert.Equal(4L, harness.Redis.Counts["rl:auth:203.0.113.50"]);
    }

    [Fact]
    public async Task RedirectHotPath_EnforcesRedirectLimitWithoutBlockingHealth()
    {
        await using var harness = new TestHarness();
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "promo42",
            Url = "https://example.com/promo",
            OwnerId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await harness.Links.AddAsync(link);

        for (var i = 0; i < 2; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/promo42");
            req.Headers.Add("X-Forwarded-For", "198.51.100.77");
            var res = await harness.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        }

        using var blockedReq = new HttpRequestMessage(HttpMethod.Get, "/promo42");
        blockedReq.Headers.Add("X-Forwarded-For", "198.51.100.77");
        var blockedRes = await harness.Client.SendAsync(blockedReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedRes.StatusCode);
        var error = await blockedRes.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));

        using var healthReq = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        healthReq.Headers.Add("X-Forwarded-For", "198.51.100.77");
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.SendAsync(healthReq)).StatusCode);
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

        public HttpClient Client { get; }
        public JwtTokenService Tokens { get; }
        public FakeRedisStringStore Redis { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryShortLinkRepository Links { get; } = new();

        public TestHarness()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-ratelimit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webRoot);

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(_webRoot);
                    builder.UseSetting("RateLimit:AuthLimit", "2");
                    builder.UseSetting("RateLimit:AuthWindowSeconds", "60");
                    builder.UseSetting("RateLimit:CreateLimit", "2");
                    builder.UseSetting("RateLimit:CreateWindowSeconds", "60");
                    builder.UseSetting("RateLimit:ApiLimit", "5");
                    builder.UseSetting("RateLimit:ApiWindowSeconds", "60");
                    builder.UseSetting("RateLimit:RedirectLimit", "2");
                    builder.UseSetting("RateLimit:RedirectWindowSeconds", "60");
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(Redis);

                        services.RemoveAll<IClickEventPublisher>();
                        services.AddSingleton<IClickEventPublisher>(new NoOpClickEventPublisher());

                        services.RemoveAll<IUserRepository>();
                        services.AddSingleton<IUserRepository>(Users);

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(Links);

                        services.RemoveAll<IClickRepository>();
                        services.AddSingleton<IClickRepository>(new InMemoryClickRepository());
                    });
                });

            Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
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

    private sealed class InMemoryShortLinkRepository : IShortLinkRepository
    {
        public Dictionary<Guid, ShortenedUrl> ById { get; } = new();
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var link) ? link : null);

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
