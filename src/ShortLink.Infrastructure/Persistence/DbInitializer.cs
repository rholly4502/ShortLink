using Microsoft.Extensions.Options;

namespace ShortLink.Infrastructure.Persistence;

public interface IDbInitializer
{
    Task InitializeAsync(bool migrateEnabled, CancellationToken ct = default);
}

public sealed class DbInitializer : IDbInitializer
{
    private readonly IMigrationRunner _migrationRunner;
    private readonly IAdminSeeder _adminSeeder;
    private readonly IOptions<AdminSeedOptions> _adminOptions;

    public DbInitializer(IMigrationRunner migrationRunner, IAdminSeeder adminSeeder, IOptions<AdminSeedOptions> adminOptions)
    {
        _migrationRunner = migrationRunner;
        _adminSeeder = adminSeeder;
        _adminOptions = adminOptions;
    }

    public async Task InitializeAsync(bool migrateEnabled, CancellationToken ct = default)
    {
        if (migrateEnabled)
        {
            await _migrationRunner.RunAsync(ct);
        }

        var o = _adminOptions.Value;
        await _adminSeeder.SeedAsync(o.Username, o.Email, o.Password, ct);
    }
}
