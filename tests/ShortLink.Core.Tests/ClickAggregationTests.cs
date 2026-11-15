using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShortLink.Core;
using ShortLink.Core.Entities;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;

namespace ShortLink.Core.Tests;

public class ClickAggregationTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public ClickAggregationTests()
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

    private static Click NewClick(Guid linkId, DateTimeOffset at, DeviceType device, string? country) => new()
    {
        Id = Guid.NewGuid(),
        ShortenedUrlId = linkId,
        Device = device,
        Country = country,
        ClickedAt = at
    };

    [Fact]
    public async Task AggregateAsync_CreatesHourlyAndDailyAggregates()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("agg1");
        var link = NewLink(owner, "agg1");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var clicks = new[]
        {
            NewClick(link.Id, new DateTimeOffset(2026, 11, 15, 6, 10, 0, TimeSpan.Zero), DeviceType.Mobile, "ID"),
            NewClick(link.Id, new DateTimeOffset(2026, 11, 15, 6, 45, 0, TimeSpan.Zero), DeviceType.Mobile, "ID"),
            NewClick(link.Id, new DateTimeOffset(2026, 11, 15, 7, 5, 0, TimeSpan.Zero), DeviceType.Desktop, "US")
        };

        var service = new ClickAggregationService(new ClickRepository(ctx));
        await service.AggregateAsync(clicks);

        var aggregates = await ctx.ClickAggregates.ToListAsync();

        // Hourly: 06:00 Mobile ID -> 2 ; 07:00 Desktop US -> 1
        var hourly06 = aggregates.Single(a => a.Period == AggregatePeriod.Hourly &&
            a.PeriodStart == new DateTimeOffset(2026, 11, 15, 6, 0, 0, TimeSpan.Zero));
        Assert.Equal("ID", hourly06.Country);
        Assert.Equal(DeviceType.Mobile, hourly06.Device);
        Assert.Equal(2L, hourly06.Count);

        var hourly07 = aggregates.Single(a => a.Period == AggregatePeriod.Hourly &&
            a.PeriodStart == new DateTimeOffset(2026, 11, 15, 7, 0, 0, TimeSpan.Zero));
        Assert.Equal(DeviceType.Desktop, hourly07.Device);
        Assert.Equal("US", hourly07.Country);
        Assert.Equal(1L, hourly07.Count);

        // Daily: 15 Nov Mobile ID -> 2 ; 15 Nov Desktop US -> 1
        var dailyMobile = aggregates.Single(a => a.Period == AggregatePeriod.Daily &&
            a.Device == DeviceType.Mobile);
        Assert.Equal(2L, dailyMobile.Count);
        Assert.Equal("ID", dailyMobile.Country);

        var dailyDesktop = aggregates.Single(a => a.Period == AggregatePeriod.Daily &&
            a.Device == DeviceType.Desktop);
        Assert.Equal(1L, dailyDesktop.Count);
        Assert.Equal("US", dailyDesktop.Country);

        Assert.Equal(4, aggregates.Count);
    }

    [Fact]
    public async Task AggregateAsync_IncrementsExistingAggregates()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("agg2");
        var link = NewLink(owner, "agg2");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var service = new ClickAggregationService(new ClickRepository(ctx));
        var at = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

        await service.AggregateAsync(new[] { NewClick(link.Id, at, DeviceType.Mobile, "ID") });
        await service.AggregateAsync(new[] { NewClick(link.Id, at.AddMinutes(5), DeviceType.Mobile, "ID") });

        var hourly = await ctx.ClickAggregates
            .SingleAsync(a => a.Period == AggregatePeriod.Hourly);
        Assert.Equal(2L, hourly.Count);
        Assert.Equal(DeviceType.Mobile, hourly.Device);

        // Only one hourly row for this bucket (upsert, not duplicate insert)
        Assert.Equal(1, await ctx.ClickAggregates.CountAsync(a => a.Period == AggregatePeriod.Hourly));
    }

    [Fact]
    public async Task AggregateAsync_EmptyClicks_AddsNothing()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("agg3");
        var link = NewLink(owner, "agg3");
        await ctx.Users.AddAsync(owner);
        await ctx.ShortLinks.AddAsync(link);
        await ctx.SaveChangesAsync();

        var service = new ClickAggregationService(new ClickRepository(ctx));
        await service.AggregateAsync(Array.Empty<Click>());

        Assert.Equal(0, await ctx.ClickAggregates.CountAsync());
    }
}
