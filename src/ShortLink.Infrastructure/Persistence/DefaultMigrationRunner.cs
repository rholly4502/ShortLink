using Microsoft.EntityFrameworkCore;

namespace ShortLink.Infrastructure.Persistence;

public sealed class DefaultMigrationRunner : IMigrationRunner
{
    private readonly AppDbContext _db;

    public DefaultMigrationRunner(AppDbContext db) => _db = db;

    public Task RunAsync(CancellationToken ct = default) => _db.Database.MigrateAsync(ct);
}
