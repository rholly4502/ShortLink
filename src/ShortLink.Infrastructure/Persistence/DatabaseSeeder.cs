using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Persistence;

public sealed class DatabaseSeeder
{
    private readonly AppDbContext _db;

    public DatabaseSeeder(AppDbContext db) => _db = db;

    public async Task SeedAsync(string adminUsername, string adminEmail, string adminPassword, CancellationToken ct = default)
    {
        var exists = await _db.Users.AnyAsync(
            u => u.Username == adminUsername || u.Email == adminEmail, ct);
        if (exists)
        {
            return;
        }

        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = adminUsername,
            Email = adminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            Role = UserRole.Admin,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }
}
