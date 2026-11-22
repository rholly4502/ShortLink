using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShortLink.Core;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;

namespace ShortLink.Core.Tests;

public class ClickIngestionTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public ClickIngestionTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new AppDbContext(options);
    }

    private static User NewUser(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = username + "@x.com",
        PasswordHash = "hash",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static ShortenedUrl NewLink(User owner, string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Url = "https://example.com/" + slug,
        OwnerId = owner.Id,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task PersistAsync_PersistsClick_WhenEventArrives()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("ing1");
        var link = NewLink(owner, "ing1");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var evt = new ClickEvent(
            ShortenedUrlId: link.Id,
            OccurredAt: new DateTimeOffset(2026, 11, 15, 6, 0, 0, TimeSpan.Zero),
            IpAddress: "203.0.113.7",
            UserAgent: "Mozilla/5.0 (test)",
            Referrer: "https://referrer.example.com/",
            Device: DeviceType.Mobile,
            Country: "ID",
            Region: "JK",
            City: "Jakarta");

        var service = new ClickIngestionService(new ClickRepository(ctx));
        await service.PersistAsync(evt);

        var click = await ctx.Clicks.SingleAsync(c => c.ShortenedUrlId == link.Id);
        Assert.Equal(link.Id, click.ShortenedUrlId);
        Assert.Equal("203.0.113.7", click.IpAddress);
        Assert.Equal("Mozilla/5.0 (test)", click.UserAgent);
        Assert.Equal("https://referrer.example.com/", click.Referrer);
        Assert.Equal(DeviceType.Mobile, click.Device);
        Assert.Equal("ID", click.Country);
        Assert.Equal("JK", click.Region);
        Assert.Equal("Jakarta", click.City);
        Assert.Equal(evt.OccurredAt, click.ClickedAt);
    }

    [Fact]
    public async Task PersistAsync_PersistsMultipleClicks_Independently()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("ing2");
        var link = NewLink(owner, "ing2");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var service = new ClickIngestionService(new ClickRepository(ctx));
        await service.PersistAsync(new ClickEvent(link.Id, DateTimeOffset.UtcNow, null, null, null, DeviceType.Desktop, null, null, null));
        await service.PersistAsync(new ClickEvent(link.Id, DateTimeOffset.UtcNow, null, null, null, DeviceType.Desktop, null, null, null));

        Assert.Equal(2, await ctx.Clicks.CountAsync(c => c.ShortenedUrlId == link.Id));
    }

    [Fact]
    public async Task PersistAsync_WithClickCounter_IncrementsShortLinkClickCount()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("ing3");
        var link = NewLink(owner, "ing3");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var service = new ClickIngestionService(new ClickRepository(ctx), new ShortLinkRepository(ctx));
        for (var i = 0; i < 3; i++)
        {
            await service.PersistAsync(new ClickEvent(
                link.Id, DateTimeOffset.UtcNow, null, null, null, DeviceType.Desktop, null, null, null));
        }

        await using var verifyCtx = CreateContext();
        Assert.Equal(3, await verifyCtx.Clicks.CountAsync(c => c.ShortenedUrlId == link.Id));
        var updatedLink = await verifyCtx.ShortLinks.SingleAsync(l => l.Id == link.Id);
        Assert.Equal(3L, updatedLink.ClickCount);
    }

    [Fact]
    public async Task PersistAsync_ClampsOversizedMetadataAndStripsNulWithoutSplittingSurrogates()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("ing4");
        var link = NewLink(owner, "ing4");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var evt = new ClickEvent(
            ShortenedUrlId: link.Id,
            OccurredAt: new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero),
            IpAddress: new string('1', 60),
            UserAgent: new string('a', 511) + "😀" + "\0tail",
            Referrer: "https://ref.example/\0" + new string('r', 2500),
            Device: DeviceType.Desktop,
            Country: "IDN",
            Region: new string('x', 150),
            City: new string('y', 150));

        var service = new ClickIngestionService(new ClickRepository(ctx));
        await service.PersistAsync(evt);

        await using var verifyCtx = CreateContext();
        var persisted = await verifyCtx.Clicks.SingleAsync(c => c.ShortenedUrlId == link.Id);
        Assert.Equal(ClickIngestionService.MaxIpAddressLength, persisted.IpAddress!.Length);
        Assert.Equal(511, persisted.UserAgent!.Length);
        Assert.False(char.IsHighSurrogate(persisted.UserAgent[^1]));
        Assert.DoesNotContain('\0', persisted.UserAgent);
        Assert.Equal(ClickIngestionService.MaxReferrerLength, persisted.Referrer!.Length);
        Assert.DoesNotContain('\0', persisted.Referrer);
        Assert.Equal("ID", persisted.Country);
        Assert.Equal(ClickIngestionService.MaxRegionLength, persisted.Region!.Length);
        Assert.Equal(ClickIngestionService.MaxCityLength, persisted.City!.Length);
    }

    [Fact]
    public async Task PersistAsync_NullArgs_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ClickIngestionService(null!));

        await using var ctx = CreateContext();
        var service = new ClickIngestionService(new ClickRepository(ctx));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.PersistAsync(null!));
    }
}
