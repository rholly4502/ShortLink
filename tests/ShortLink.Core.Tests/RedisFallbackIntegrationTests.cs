using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShortLink.Api.Models;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

/// <summary>Integration tests end-to-end saat Redis tidak tersedia (fail-open rate limiter, fallback PG redirect, best-effort cache invalidation).</summary>
public sealed class RedisFallbackIntegrationTests
{
    [Fact]
    public async Task Redirect_WhenRedisUnavailable_FallsBackToRepositoryReturns302AndEmitsClickEvent()
    {
        await using var harness = new TestHarness();
        var owner = CreateUser("alice", "alice@example.com");
        harness.Users.Add(owner);
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "abc123",
            Url = "https://example.com/target",
            OwnerId = owner.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        await harness.Links.AddAsync(link);

        var res = await harness.Client.GetAsync("/abc123");

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("https://example.com/target", res.Headers.Location?.ToString());
        var evt = Assert.Single(harness.ClickPublisher.Published);
        Assert.Equal(link.Id, evt.ShortenedUrlId);
    }

    [Fact]
    public async Task Redirect_ExpiredLink_WhenRedisUnavailable_Returns404WithoutClickEvent()
    {
        await using var harness = new TestHarness();
        var owner = CreateUser("alice", "alice@example.com");
        harness.Users.Add(owner);
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "expired1",
            Url = "https://example.com/expired",
            OwnerId = owner.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        await harness.Links.AddAsync(link);

        var res = await harness.Client.GetAsync("/expired1");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty(harness.ClickPublisher.Published);
    }

    [Fact]
    public async Task ProtectedEndpoints_ListUpdateDelete_WhenRedisUnavailable_SucceedGracefully()
    {
        await using var harness = new TestHarness();
        var alice = CreateUser("alice", "alice@example.com");
        var bob = CreateUser("bob", "bob@example.com");
        harness.Users.Add(alice);
        harness.Users.Add(bob);

        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "mySlug1",
            Url = "https://example.com/initial",
            OwnerId = alice.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await harness.Links.AddAsync(link);

        var aliceTokens = harness.Tokens.Generate(alice);
        var bobTokens = harness.Tokens.Generate(bob);

        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.AccessToken);
        var listRes = await harness.Client.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var listed = await listRes.Content.ReadFromJsonAsync<List<LinkResponse>>();
        Assert.NotNull(listed);
        Assert.Single(listed!);

        using var bobPutReq = new HttpRequestMessage(HttpMethod.Put, $"/api/links/{link.Id}")
        {
            Content = JsonContent.Create(new UpdateLinkRequest("https://example.org/hijacked", null))
        };
        bobPutReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobPutReq)).StatusCode);

        using var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/links/{link.Id}")
        {
            Content = JsonContent.Create(new UpdateLinkRequest("https://example.org/updated", null))
        };
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.AccessToken);
        var putRes = await harness.Client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);
        var updated = await putRes.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(updated);
        Assert.Equal("https://example.org/updated", updated!.Url);
        Assert.Equal("https://example.org/updated", harness.Links.ById[link.Id].Url);

        using var bobDelReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{link.Id}");
        bobDelReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobDelReq)).StatusCode);
        Assert.True(harness.Links.ById.ContainsKey(link.Id));

        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{link.Id}");
        delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.AccessToken);
        var delRes = await harness.Client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);
        Assert.False(harness.Links.ById.ContainsKey(link.Id));
    }

    [Fact]
    public async Task AuthRefresh_WhenRedisUnavailable_FailsOpenAndReturnsOk()
    {
        await using var harness = new TestHarness();
        var alice = CreateUser("alice", "alice@example.com");
        harness.Users.Add(alice);
        var pair = harness.Tokens.Generate(alice);

        var res = await harness.Client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(pair.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.Equal(alice.Id, body!.UserId);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
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
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryShortLinkRepository Links { get; } = new();
        public InMemoryClickRepository Clicks { get; } = new();
        public RecordingClickEventPublisher ClickPublisher { get; } = new();

        public TestHarness()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-redis-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webRoot);

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseWebRoot(_webRoot);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IRedisStringStore>();
                        services.AddSingleton<IRedisStringStore>(new FaultyRedisStringStore());

                        services.RemoveAll<IClickEventPublisher>();
                        services.AddSingleton<IClickEventPublisher>(ClickPublisher);

                        services.RemoveAll<IUserRepository>();
                        services.AddSingleton<IUserRepository>(Users);

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(Links);

                        services.RemoveAll<IClickRepository>();
                        services.AddSingleton<IClickRepository>(Clicks);
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
        public Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(new List<ClickAggregate>());
    }
}
