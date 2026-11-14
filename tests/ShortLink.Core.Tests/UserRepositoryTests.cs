using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Entities;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Persistence.Repositories;

namespace ShortLink.Core.Tests;

public sealed class UserRepositoryTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public UserRepositoryTests()
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
        Role = UserRole.User,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task GetByUsername_ReturnsUser_WhenExists()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var u = NewUser("alice");
        await ctx.Users.AddAsync(u);
        await ctx.SaveChangesAsync();

        var repo = new UserRepository(ctx);
        var found = await repo.GetByUsernameAsync("alice");

        Assert.NotNull(found);
        Assert.Equal(u.Id, found!.Id);
    }

    [Fact]
    public async Task GetByUsername_ReturnsNull_WhenMissing()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        var repo = new UserRepository(ctx);
        Assert.Null(await repo.GetByUsernameAsync("nobody"));
    }

    [Fact]
    public async Task GetByEmail_ReturnsUser_WhenExists()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var u = NewUser("bob");
        await ctx.Users.AddAsync(u);
        await ctx.SaveChangesAsync();

        var repo = new UserRepository(ctx);
        var found = await repo.GetByEmailAsync("bob@x.com");

        Assert.NotNull(found);
        Assert.Equal(u.Id, found!.Id);
    }

    [Fact]
    public async Task GetById_ReturnsUser_WhenExists()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var u = NewUser("carol");
        await ctx.Users.AddAsync(u);
        await ctx.SaveChangesAsync();

        var repo = new UserRepository(ctx);
        Assert.NotNull(await repo.GetByIdAsync(u.Id));
    }

    [Fact]
    public async Task Exists_MatchesUsernameOrEmail()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        await ctx.Users.AddAsync(NewUser("dave"));
        await ctx.SaveChangesAsync();

        var repo = new UserRepository(ctx);
        Assert.True(await repo.ExistsAsync("dave", "someone@x.com"));
        Assert.True(await repo.ExistsAsync("someone", "dave@x.com"));
        Assert.False(await repo.ExistsAsync("nobody", "nobody@x.com"));
    }

    [Fact]
    public async Task Add_PersistsUser()
    {
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        var u = NewUser("erin");
        var repo = new UserRepository(ctx);
        await repo.AddAsync(u);

        Assert.Equal(1, await ctx.Users.CountAsync(x => x.Id == u.Id));
    }
}
