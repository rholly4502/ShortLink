using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ShortLink.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddShortLinkPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")));

        services.AddScoped<IAdminSeeder, DatabaseSeeder>();
        services.AddScoped<IMigrationRunner, DefaultMigrationRunner>();
        services.AddScoped<IDbInitializer, DbInitializer>();
        services.Configure<AdminSeedOptions>(configuration.GetSection("AdminSeed"));

        return services;
    }
}
