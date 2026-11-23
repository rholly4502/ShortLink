using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShortLink.Api.Controllers;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

public sealed class AnalyticsCsvExportTests
{
    private readonly FixedClock _clock = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    // --- Pure AnalyticsCsvExporter unit tests ---

    [Fact]
    public void Export_NullSummary_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AnalyticsCsvExporter.Export(null!));
    }

    [Fact]
    public void Export_PopulatedSummary_FormatsHeaderSummaryDayDeviceAndCountryRows()
    {
        var linkId = Guid.NewGuid();
        var summary = new AnalyticsSummary(
            linkId,
            12,
            [new DeviceStat("Desktop", 8), new DeviceStat("Mobile", 4)],
            [new CountryStat("ID", 10), new CountryStat("US", 2)],
            [
                new DayStat(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), 5),
                new DayStat(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), 7)
            ]);

        var csv = AnalyticsCsvExporter.Export(summary);

        var expected =
            "section,label,clicks\n" +
            "summary,total,12\n" +
            "day,2026-11-16,5\n" +
            "day,2026-11-17,7\n" +
            "device,Desktop,8\n" +
            "device,Mobile,4\n" +
            "country,ID,10\n" +
            "country,US,2\n";
        Assert.Equal(expected, csv);
    }

    [Fact]
    public void Export_EmptyBreakdowns_OutputsHeaderAndZeroTotal()
    {
        var summary = new AnalyticsSummary(Guid.NewGuid(), 0, [], [], []);

        var csv = AnalyticsCsvExporter.Export(summary);

        Assert.Equal("section,label,clicks\nsummary,total,0\n", csv);
    }

    [Fact]
    public void Export_DayWithNonUtcOffset_PreservesCalendarDate()
    {
        var summary = new AnalyticsSummary(
            Guid.NewGuid(),
            3,
            [],
            [],
            [new DayStat(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.FromHours(7)), 3)]);

        var csv = AnalyticsCsvExporter.Export(summary);

        Assert.Equal("section,label,clicks\nsummary,total,3\nday,2026-11-16,3\n", csv);
    }

    [Fact]
    public void Export_SpecialCsvCharacters_EscapesWithRfc4180Quotes()
    {
        var summary = new AnalyticsSummary(
            Guid.NewGuid(),
            6,
            [
                new DeviceStat("Desktop, Linux", 2),
                new DeviceStat("Bot \"Crawler\"", 1),
                new DeviceStat("Line1\nLine2", 3)
            ],
            [],
            []);

        var csv = AnalyticsCsvExporter.Export(summary);

        var expected =
            "section,label,clicks\n" +
            "summary,total,6\n" +
            "device,\"Desktop, Linux\",2\n" +
            "device,\"Bot \"\"Crawler\"\"\",1\n" +
            "device,\"Line1\nLine2\",3\n";
        Assert.Equal(expected, csv);
    }

    [Theory]
    [InlineData("=CMD|'/C calc'!A0", "'=CMD|'/C calc'!A0")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
    [InlineData("  =1+1", "'  =1+1")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("\r=1+1", "\"'\r=1+1\"")]
    [InlineData("=HYPERLINK(\"https://evil.test\",\"click\")", "\"'=HYPERLINK(\"\"https://evil.test\"\",\"\"click\"\")\"")]
    public void Export_FormulaInjectionPayloads_PrefixedWithSingleQuote(string payload, string expectedField)
    {
        var escaped = AnalyticsCsvExporter.EscapeField(payload);
        Assert.Equal(expectedField, escaped);

        var summary = new AnalyticsSummary(
            Guid.NewGuid(),
            1,
            [],
            [new CountryStat(payload, 1)],
            []);
        var csv = AnalyticsCsvExporter.Export(summary);

        Assert.Equal($"section,label,clicks\nsummary,total,1\ncountry,{expectedField},1\n", csv);
    }

    [Fact]
    public void ExportUtf8_ProducesBomFreeUtf8Bytes()
    {
        var summary = new AnalyticsSummary(
            Guid.NewGuid(),
            1,
            [new DeviceStat("Other", 1)],
            [],
            []);

        var bytes = AnalyticsCsvExporter.ExportUtf8(summary);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("section,label,clicks\nsummary,total,1\ndevice,Other,1\n", Encoding.UTF8.GetString(bytes));
    }

    // --- ShortLinksController.ExportAnalyticsCsv unit tests ---

    [Fact]
    public async Task Controller_ExportAnalyticsCsv_AsOwner_ReturnsCsvFileWithNoSniffHeader()
    {
        var repo = new InMemoryShortLinkRepository();
        var clicks = new InMemoryClickRepository();
        var controller = NewController(_owner, repo, clicks);
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "exp1",
            Url = "https://example.com/exp1",
            OwnerId = _owner,
            CreatedAt = _clock.UtcNow
        };
        await repo.AddAsync(link);
        clicks.Aggregates.Add(new ClickAggregate
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = link.Id,
            PeriodStart = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero),
            Period = AggregatePeriod.Daily,
            Device = DeviceType.Desktop,
            Country = "ID",
            Count = 4
        });

        var result = await controller.ExportAnalyticsCsv(link.Id, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal($"analytics-{link.Id:D}.csv", file.FileDownloadName);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());

        var csv = Encoding.UTF8.GetString(file.FileContents);
        Assert.Equal(
            "section,label,clicks\n" +
            "summary,total,4\n" +
            "day,2026-11-20,4\n" +
            "device,Desktop,4\n" +
            "country,ID,4\n",
            csv);
    }

    [Fact]
    public async Task Controller_ExportAnalyticsCsv_AsNonOwner_ReturnsNotFound()
    {
        var repo = new InMemoryShortLinkRepository();
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "exp2",
            Url = "https://example.com/exp2",
            OwnerId = _owner,
            CreatedAt = _clock.UtcNow
        };
        await repo.AddAsync(link);
        var controller = NewController(_other, repo, new InMemoryClickRepository());

        var result = await controller.ExportAnalyticsCsv(link.Id, default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Controller_ExportAnalyticsCsv_UnknownLink_ReturnsNotFound()
    {
        var controller = NewController(_owner, new InMemoryShortLinkRepository(), new InMemoryClickRepository());

        var result = await controller.ExportAnalyticsCsv(Guid.NewGuid(), default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Controller_ExportAnalyticsCsv_InvalidIdentity_ReturnsUnauthorized()
    {
        var repo = new InMemoryShortLinkRepository();
        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "exp3",
            Url = "https://example.com/exp3",
            OwnerId = _owner,
            CreatedAt = _clock.UtcNow
        };
        await repo.AddAsync(link);
        var controller = NewController(Guid.Empty, repo, new InMemoryClickRepository(), includeClaims: false);

        var result = await controller.ExportAnalyticsCsv(link.Id, default);

        Assert.IsType<UnauthorizedResult>(result);
    }

    // --- End-to-end integration tests ---

    [Fact]
    public async Task EndToEnd_ExportAnalyticsCsv_AsOwner_ReturnsCsvWithExpectedHeadersAndBody()
    {
        await using var harness = new TestHarness();
        var alice = CreateUser("alice", "alice@example.com");
        harness.Users.Add(alice);
        var tokens = harness.Tokens.Generate(alice);

        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "e2ecsv",
            Url = "https://example.com/target",
            OwnerId = alice.Id,
            CreatedAt = _clock.UtcNow
        };
        await harness.Links.AddAsync(link);
        harness.Clicks.Aggregates.Add(new ClickAggregate
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = link.Id,
            PeriodStart = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero),
            Period = AggregatePeriod.Daily,
            Device = DeviceType.Mobile,
            Country = "SG",
            Count = 9
        });

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{link.Id}/analytics/csv");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var res = await harness.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", res.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"analytics-{link.Id:D}.csv", res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.True(res.Headers.TryGetValues("X-Content-Type-Options", out var noSniff));
        Assert.Contains("nosniff", noSniff);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(
            "section,label,clicks\n" +
            "summary,total,9\n" +
            "day,2026-11-21,9\n" +
            "device,Mobile,9\n" +
            "country,SG,9\n",
            body);

        // Verify SPA fallback and API auth routing remain intact.
        var spaRes = await harness.Client.GetAsync("/dashboard/deep");
        Assert.Equal(HttpStatusCode.OK, spaRes.StatusCode);
        Assert.Contains("ShortLink SPA", await spaRes.Content.ReadAsStringAsync());

        var unauthRes = await harness.Client.GetAsync($"/api/links/{link.Id}/analytics/csv");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_ExportAnalyticsCsv_RejectsUnauthenticatedRefreshTokenAndNonOwner()
    {
        await using var harness = new TestHarness();
        var alice = CreateUser("alice", "alice@example.com");
        var bob = CreateUser("bob", "bob@example.com");
        harness.Users.Add(alice);
        harness.Users.Add(bob);
        var aliceTokens = harness.Tokens.Generate(alice);
        var bobTokens = harness.Tokens.Generate(bob);

        var link = new ShortenedUrl
        {
            Id = Guid.NewGuid(),
            Slug = "ownedcsv",
            Url = "https://example.com/owned",
            OwnerId = alice.Id,
            CreatedAt = _clock.UtcNow
        };
        await harness.Links.AddAsync(link);

        var unauthRes = await harness.Client.GetAsync($"/api/links/{link.Id}/analytics/csv");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        using var refreshReq = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{link.Id}/analytics/csv");
        refreshReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceTokens.RefreshToken);
        var refreshRes = await harness.Client.SendAsync(refreshReq);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshRes.StatusCode);

        using var bobReq = new HttpRequestMessage(HttpMethod.Get, $"/api/links/{link.Id}/analytics/csv");
        bobReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobTokens.AccessToken);
        var bobRes = await harness.Client.SendAsync(bobReq);
        Assert.Equal(HttpStatusCode.NotFound, bobRes.StatusCode);
    }

    // --- Helpers & fakes ---

    private ShortLinksController NewController(
        Guid caller,
        IShortLinkRepository repo,
        IClickRepository clicks,
        bool includeClaims = true)
    {
        var service = new ShortLinkService(repo, new StubSlugGenerator(), new UrlValidator(), _clock, clicks);
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

    private sealed class StubSlugGenerator : ISlugGenerator
    {
        public string Generate(int length = 7) => "stub123";
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
        public List<ClickAggregate> Aggregates { get; } = new();

        public Task AddAsync(Click click, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<Click> clicks, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAggregateAsync(ClickAggregate aggregate, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAggregatesAsync(IEnumerable<ClickAggregate> aggregates, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<ClickAggregate>> GetByShortLinkIdAsync(Guid shortenedUrlId, CancellationToken ct = default)
            => Task.FromResult(Aggregates.Where(a => a.ShortenedUrlId == shortenedUrlId).ToList());
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

    private sealed class NoOpClickEventPublisher : IClickEventPublisher
    {
        public Task PublishAsync(ClickEvent clickEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _webRoot;
        private readonly WebApplicationFactory<Program> _factory;

        public HttpClient Client { get; }
        public JwtTokenService Tokens { get; }
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryShortLinkRepository Links { get; } = new();
        public InMemoryClickRepository Clicks { get; } = new();

        public TestHarness()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "shortlink-csv-test-" + Guid.NewGuid().ToString("N"));
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
                        services.AddSingleton<IClickRepository>(Clicks);
                    });
                });

            Client = _factory.CreateClient();
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
}
