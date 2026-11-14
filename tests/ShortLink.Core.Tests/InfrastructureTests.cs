using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Entities;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Core.Tests;

public class InfrastructureTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public InfrastructureTests()
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

    private static ShortenedUrl NewLink(User owner, string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Url = "https://example.com/" + slug,
        OwnerId = owner.Id,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static User NewUser(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = username + "@x.com",
        PasswordHash = "hash",
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SlugUniqueIndex_RejectsDuplicateSlug()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var owner = NewUser("owner1");
        ctx.Users.Add(owner);
        await ctx.SaveChangesAsync();

        ctx.ShortLinks.Add(NewLink(owner, "abc"));
        await ctx.SaveChangesAsync();

        ctx.ShortLinks.Add(NewLink(owner, "abc"));
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task UsernameUniqueIndex_RejectsDuplicateUsername()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Users.Add(NewUser("alice"));
        await ctx.SaveChangesAsync();

        ctx.Users.Add(NewUser("alice"));
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task EmailUniqueIndex_RejectsDuplicateEmail()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Users.Add(NewUser("bob"));
        await ctx.SaveChangesAsync();

        ctx.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "bob2",
            Email = "bob@x.com",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Seeder_SeedsAdmin_WhenNoAdminExists()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        var seeder = new DatabaseSeeder(ctx);
        await seeder.SeedAsync("admin", "admin@shortlink.dev", "P@ssw0rd!");

        var admin = await ctx.Users.SingleAsync(u => u.Username == "admin");
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Equal("admin@shortlink.dev", admin.Email);
        Assert.True(BCrypt.Net.BCrypt.Verify("P@ssw0rd!", admin.PasswordHash));
    }

    [Fact]
    public async Task Seeder_IsIdempotent_KeepsSingleAdmin()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        var seeder = new DatabaseSeeder(ctx);
        await seeder.SeedAsync("admin", "admin@shortlink.dev", "FirstPass1!");
        await seeder.SeedAsync("admin", "admin@shortlink.dev", "SecondPass2!");

        Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "admin"));
    }
}
