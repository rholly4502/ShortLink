using Microsoft.Extensions.Options;
using ShortLink.Infrastructure.Persistence;

namespace ShortLink.Core.Tests;

public class DbInitializerTests
{
    private sealed class FakeMigrationRunner : IMigrationRunner
    {
        public int RunCount;
        public Task RunAsync(CancellationToken ct = default) { RunCount++; return Task.CompletedTask; }
    }

    private sealed class FakeAdminSeeder : IAdminSeeder
    {
        public int SeedCount;
        public string? Username;
        public Task SeedAsync(string username, string email, string password, CancellationToken ct = default)
        {
            SeedCount++;
            Username = username;
            return Task.CompletedTask;
        }
    }

    private static AdminSeedOptions SeedOptions() => new()
    {
        Username = "admin",
        Email = "admin@shortlink.dev",
        Password = "pw1234",
    };

    [Fact]
    public async Task InitializeAsync_WhenMigrationsEnabled_RunsMigrationThenSeeds()
    {
        var runner = new FakeMigrationRunner();
        var seeder = new FakeAdminSeeder();
        var init = new DbInitializer(runner, seeder, Options.Create(SeedOptions()));

        await init.InitializeAsync(migrateEnabled: true);

        Assert.Equal(1, runner.RunCount);
        Assert.Equal(1, seeder.SeedCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenMigrationsDisabled_SkipsMigrationButStillSeeds()
    {
        var runner = new FakeMigrationRunner();
        var seeder = new FakeAdminSeeder();
        var init = new DbInitializer(runner, seeder, Options.Create(SeedOptions()));

        await init.InitializeAsync(migrateEnabled: false);

        Assert.Equal(0, runner.RunCount);
        Assert.Equal(1, seeder.SeedCount);
    }

    [Fact]
    public async Task InitializeAsync_PassesAdminOptionsToSeeder()
    {
        var seeder = new FakeAdminSeeder();
        var init = new DbInitializer(new FakeMigrationRunner(), seeder, Options.Create(SeedOptions()));

        await init.InitializeAsync(migrateEnabled: false);

        Assert.Equal("admin", seeder.Username);
    }
}
