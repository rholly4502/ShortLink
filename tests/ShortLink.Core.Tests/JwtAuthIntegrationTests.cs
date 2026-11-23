using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShortLink.Api.Models;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

/// <summary>Integration tests end-to-end untuk pipeline JWT Bearer + otorisasi kepemilikan link.</summary>
public sealed class JwtAuthIntegrationTests
{
    [Fact]
    public async Task ProtectedEndpoint_WithValidAccessToken_ReturnsOk()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var pair = harness.Tokens.Generate(user);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);

        var res = await harness.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithRefreshTokenAsBearer_ReturnsUnauthorized()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var pair = harness.Tokens.Generate(user);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.RefreshToken);
        var getRes = await harness.Client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.Unauthorized, getRes.StatusCode);

        using var postReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com", null))
        };
        postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.RefreshToken);
        var postRes = await harness.Client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Unauthorized, postRes.StatusCode);
        Assert.Empty(harness.Links.ById);
    }

    [Fact]
    public async Task RefreshEndpoint_WithValidRefreshToken_ReturnsNewTokenPair()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("alice", "alice@example.com");
        harness.Users.Add(user);
        var pair = harness.Tokens.Generate(user);

        var res = await harness.Client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(pair.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.Equal(user.Id, body.UserId);
    }

    [Fact]
    public async Task RegisterAndLogin_EndToEnd_EnforcesTrimmedUniquenessAndPasswordBounds()
    {
        await using var harness = CreateHarness();

        var regRes = await harness.Client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("  alice01  ", "  alice01@example.com  ", "StrongPass!23"));
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);

        // Pendaftaran duplikat dengan spasi pinggir wajib ditolak 409 Conflict
        var dupRes = await harness.Client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("alice01", "other@example.com", "StrongPass!23"));
        Assert.Equal(HttpStatusCode.Conflict, dupRes.StatusCode);

        // Login dengan identifier berspasi pinggir tetap berhasil
        var loginRes = await harness.Client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("  alice01  ", "StrongPass!23"));
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        // Password > 72 byte ditolak 400 BadRequest
        var oversizedLoginRes = await harness.Client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("alice01", new string('p', 73)));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedLoginRes.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithTokenMissingTokenTypeClaim_ReturnsUnauthorized()
    {
        await using var harness = CreateHarness();
        var token = WriteCustomJwt(
            harness.JwtOptions,
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()));

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await harness.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithEmptyGuidSubjectAccessToken_ReturnsUnauthorized()
    {
        await using var harness = CreateHarness();
        var token = WriteCustomJwt(
            harness.JwtOptions,
            new Claim(JwtRegisteredClaimNames.Sub, Guid.Empty.ToString()),
            new Claim(JwtTokenService.TokenTypeClaim, JwtTokenService.AccessTokenType));

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await harness.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoints_WithUnknownUserIdAccessToken_ReturnsUnauthorizedAndDoesNotCreateOrMutateLinks()
    {
        await using var harness = CreateHarness();
        var alice = CreateUser("alice", "alice@example.com");
        harness.Users.Add(alice);
        var aliceLink = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "alice01",
            Url = "https://example.com/alice",
            OwnerId = alice.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await harness.Links.AddAsync(aliceLink);

        var ghostUser = CreateUser("ghost", "ghost@example.com");
        var ghostTokens = harness.Tokens.Generate(ghostUser);

        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ghostTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(listReq)).StatusCode);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/ghost", null))
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ghostTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(createReq)).StatusCode);
        Assert.Single(harness.Links.ById);

        using var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/links/{aliceLink.Id}")
        {
            Content = JsonContent.Create(new UpdateLinkRequest("https://example.org/hijack", null))
        };
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ghostTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(putReq)).StatusCode);

        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{aliceLink.Id}");
        delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ghostTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(delReq)).StatusCode);
        Assert.True(harness.Links.ById.ContainsKey(aliceLink.Id));
    }

    [Fact]
    public async Task LinksEndpoints_CrossUserOwnership_EnforcedEndToEnd()
    {
        await using var harness = CreateHarness();
        var alice = CreateUser("alice", "alice@example.com");
        var bob = CreateUser("bob", "bob@example.com");
        harness.Users.Add(alice);
        harness.Users.Add(bob);

        var aliceTokens = harness.Tokens.Generate(alice);
        var bobTokens = harness.Tokens.Generate(bob);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest("https://example.com/alice-target", null))
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.AccessToken);
        var createRes = await harness.Client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(created);

        using var bobGetReq = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{created!.Id}");
        bobGetReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobGetReq)).StatusCode);

        using var bobPutReq = new HttpRequestMessage(HttpMethod.Put, $"/api/links/{created.Id}")
        {
            Content = JsonContent.Create(new UpdateLinkRequest("https://example.org/hijacked", null))
        };
        bobPutReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobPutReq)).StatusCode);

        using var bobDelReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/links/{created.Id}");
        bobDelReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobDelReq)).StatusCode);

        using var bobAnalyticsReq = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{created.Id}/analytics");
        bobAnalyticsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobAnalyticsReq)).StatusCode);

        using var bobCsvReq = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{created.Id}/analytics/csv");
        bobCsvReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobCsvReq)).StatusCode);

        // Non-owner PUT with invalid URL or past expiry still returns 404 NotFound (no existence oracle via 400 BadRequest).
        using var bobPutInvalidUrlReq = new HttpRequestMessage(HttpMethod.Put, $"/api/links/{created.Id}")
        {
            Content = JsonContent.Create(new UpdateLinkRequest("javascript:alert(1)", DateTimeOffset.UtcNow.AddDays(-1)))
        };
        bobPutInvalidUrlReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.SendAsync(bobPutInvalidUrlReq)).StatusCode);

        using var aliceGetReq = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{created.Id}");
        aliceGetReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.AccessToken);
        var aliceGetRes = await harness.Client.SendAsync(aliceGetReq);
        Assert.Equal(HttpStatusCode.OK, aliceGetRes.StatusCode);
        var fetched = await aliceGetRes.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(fetched);
        Assert.Equal("https://example.com/alice-target", fetched!.Url);
    }

    private static TestHarness CreateHarness() => new();

    private static User CreateUser(string username, string email) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = email,
        PasswordHash = "hash",
        Role = UserRole.User,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static string WriteCustomJwt(JwtOptions jwtOptions, params Claim[] claims)
    {
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _webRoot;
        private readonly WebApplicationFactory<Program> _factory;

        public HttpClient Client { get; }
        public JwtOptions JwtOptions { get; }
        public JwtTokenService Tokens { get; }
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryShortLinkRepository Links { get; } = new();
        public InMemoryClickRepository Clicks { get; } = new();

        public TestHarness()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-jwt-test-" + Guid.NewGuid().ToString("N"));
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
                        services.AddSingleton<IClickEventPublisher>(new NoOpClickEventPublisher());

                        services.RemoveAll<IUserRepository>();
                        services.AddSingleton<IUserRepository>(Users);

                        services.RemoveAll<IShortLinkRepository>();
                        services.AddSingleton<IShortLinkRepository>(Links);

                        services.RemoveAll<IClickRepository>();
                        services.AddSingleton<IClickRepository>(Clicks);
                    });
                });

            Client = _factory.CreateClient();
            JwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
            Tokens = new JwtTokenService(Options.Create(JwtOptions));
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
        public Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(new List<ClickAggregate>());
    }
}
