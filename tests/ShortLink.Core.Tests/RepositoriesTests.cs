using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;

namespace ShortLink.Core.Tests;

public class RepositoriesTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public RepositoriesTests()
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

    // ---- IShortLinkRepository ----

    [Fact]
    public async Task GetBySlug_ReturnsLink_WhenExists()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o1");
        var link = NewLink(owner, "abc");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        var found = await repo.GetBySlugAsync("abc");

        Assert.NotNull(found);
        Assert.Equal(link.Id, found!.Id);
    }

    [Fact]
    public async Task GetBySlug_ReturnsNull_WhenMissing()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var repo = new ShortLinkRepository(ctx);

        Assert.Null(await repo.GetBySlugAsync("nope"));
    }

    [Fact]
    public async Task GetById_ReturnsCorrectLink()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o2");
        var a = NewLink(owner, "aaa");
        var b = NewLink(owner, "bbb");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddRangeAsync(a, b);
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        var found = await repo.GetByIdAsync(b.Id);

        Assert.NotNull(found);
        Assert.Equal(b.Slug, found!.Slug);
    }

    [Fact]
    public async Task GetByOwner_ReturnsOnlyOwnersLinks()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var ownerA = NewUser("oA");
        var ownerB = NewUser("oB");
        await ctx.Users.AddRangeAsync(ownerA, ownerB);
        await ctx.ShortLinks.AddRangeAsync(NewLink(ownerA, "a1"), NewLink(ownerA, "a2"), NewLink(ownerB, "b1"));
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        var links = await repo.GetByOwnerAsync(ownerA.Id);

        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(ownerA.Id, l.OwnerId));
    }

    [Fact]
    public async Task SlugExists_ReflectsPresence()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o3");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(NewLink(owner, "xyz"));
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        Assert.True(await repo.SlugExistsAsync("xyz"));
        Assert.False(await repo.SlugExistsAsync("zzz"));
    }

    [Fact]
    public async Task Add_PersistsLink()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o4");
        await ctx.Users.AddAsync(owner);
        await ctx.SaveChangesAsync();

        var link = NewLink(owner, "add1");
        var repo = new ShortLinkRepository(ctx);
        await repo.AddAsync(link);

        Assert.Equal(1, await ctx.ShortLinks.CountAsync(l => l.Slug == "add1"));
    }

    [Fact]
    public async Task Update_PersistsChanges()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o5");
        var link = NewLink(owner, "upd");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        link.Url = "https://changed.example.com/";
        var repo = new ShortLinkRepository(ctx);
        await repo.UpdateAsync(link);

        var reloaded = await ctx.ShortLinks.SingleAsync(l => l.Id == link.Id);
        Assert.Equal("https://changed.example.com/", reloaded.Url);
    }

    [Fact]
    public async Task Delete_RemovesLink()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o6");
        var link = NewLink(owner, "del");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        await repo.DeleteAsync(link);

        Assert.Equal(0, await ctx.ShortLinks.CountAsync());
    }

    [Fact]
    public async Task IncrementClickCount_IncrementsAtomicallyWithoutAffectingOtherLinks()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("oInc");
        var linkA = NewLink(owner, "incA");
        var linkB = NewLink(owner, "incB");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddRangeAsync(linkA, linkB);
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        await repo.IncrementClickCountAsync(linkA.Id);
        await repo.IncrementClickCountAsync(linkA.Id, delta: 3);

        var reloadedA = await ctx.ShortLinks.AsNoTracking().SingleAsync(l => l.Id == linkA.Id);
        var reloadedB = await ctx.ShortLinks.AsNoTracking().SingleAsync(l => l.Id == linkB.Id);
        Assert.Equal(4L, reloadedA.ClickCount);
        Assert.Equal(0L, reloadedB.ClickCount);
    }

    [Fact]
    public async Task IncrementClickCount_InvalidArguments_Throws()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var repo = new ShortLinkRepository(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() => repo.IncrementClickCountAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.IncrementClickCountAsync(Guid.NewGuid(), delta: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.IncrementClickCountAsync(Guid.NewGuid(), delta: -5));
    }

    [Fact]
    public async Task IncrementClickCount_UnknownLinkId_CompletesWithoutThrowing()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("oUnknown");
        var link = NewLink(owner, "known1");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var repo = new ShortLinkRepository(ctx);
        await repo.IncrementClickCountAsync(Guid.NewGuid());

        var reloaded = await ctx.ShortLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        Assert.Equal(0L, reloaded.ClickCount);
    }

    // ---- IClickRepository ----

    private static Click NewClick(ShortenedUrl link) => new()
    {
        Id = Guid.NewGuid(),
        ShortenedUrlId = link.Id,
        IpAddress = "1.2.3.4",
        UserAgent = "UA",
        Device = DeviceType.Mobile,
        Country = "ID",
        ClickedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task AddClick_PersistsClick()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o7");
        var link = NewLink(owner, "c1");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var click = NewClick(link);
        var repo = new ClickRepository(ctx);
        await repo.AddAsync(click);

        var saved = await ctx.Clicks.SingleAsync(c => c.Id == click.Id);
        Assert.Equal(link.Id, saved.ShortenedUrlId);
        Assert.Equal(DeviceType.Mobile, saved.Device);
    }

    [Fact]
    public async Task AddRange_PersistsAllClicks()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o8");
        var link = NewLink(owner, "c2");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var repo = new ClickRepository(ctx);
        await repo.AddRangeAsync(new[] { NewClick(link), NewClick(link), NewClick(link) });

        Assert.Equal(3, await ctx.Clicks.CountAsync(c => c.ShortenedUrlId == link.Id));
    }

    [Fact]
    public async Task AddAggregate_PersistsAggregate()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("o9");
        var link = NewLink(owner, "c3");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var agg = new ClickAggregate
        {
            Id = Guid.NewGuid(),
            ShortenedUrlId = link.Id,
            PeriodStart = DateTimeOffset.UtcNow.Date,
            Period = AggregatePeriod.Hourly,
            Device = DeviceType.Desktop,
            Country = "US",
            Count = 5
        };

        var repo = new ClickRepository(ctx);
        await repo.AddAggregateAsync(agg);

        var saved = await ctx.ClickAggregates.SingleAsync(a => a.Id == agg.Id);
        Assert.Equal(5, saved.Count);
        Assert.Equal("US", saved.Country);
    }
    // ---- IClickRepository ----

    [Fact]
    public async Task GetByShortLinkId_ReturnsOnlyThatLinksAggregates()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("oAna");
        var linkA = NewLink(owner, "anaa");
        var linkB = NewLink(owner, "anab");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddRangeAsync(linkA, linkB);
        await ctx.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow.Date;
        var aggA = new ClickAggregate { Id = Guid.NewGuid(), ShortenedUrlId = linkA.Id, PeriodStart = now, Period = AggregatePeriod.Daily, Device = DeviceType.Desktop, Country = "ID", Count = 3 };
        var aggB = new ClickAggregate { Id = Guid.NewGuid(), ShortenedUrlId = linkB.Id, PeriodStart = now, Period = AggregatePeriod.Daily, Device = DeviceType.Mobile, Country = "US", Count = 1 };
        await ctx.ClickAggregates.AddRangeAsync(aggA, aggB);
        await ctx.SaveChangesAsync();

        var repo = new ClickRepository(ctx);
        var got = await repo.GetByShortLinkIdAsync(linkA.Id);

        Assert.Single(got);
        Assert.Equal(aggA.Id, got[0].Id);
    }
}
