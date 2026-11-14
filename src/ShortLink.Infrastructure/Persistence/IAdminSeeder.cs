namespace ShortLink.Infrastructure.Persistence;

public interface IAdminSeeder
{
    Task SeedAsync(string username, string email, string password, CancellationToken ct = default);
}
