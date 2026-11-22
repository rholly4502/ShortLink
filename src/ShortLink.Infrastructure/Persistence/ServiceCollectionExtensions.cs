using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Infrastructure.Persistence.Repositories;
using ShortLink.Infrastructure.Security;

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

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IShortLinkRepository, ShortLinkRepository>();
        services.AddScoped<IShortLinkClickCounter, ShortLinkRepository>();
        services.AddScoped<IClickRepository, ClickRepository>();

        services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<IAuthTokenService, JwtTokenService>();
        services.AddScoped<AuthService>();
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISlugGenerator, SlugGenerator>();
        services.AddSingleton<IUrlValidator, UrlValidator>();
        services.AddScoped<ShortLinkService>();

        return services;
    }
}
