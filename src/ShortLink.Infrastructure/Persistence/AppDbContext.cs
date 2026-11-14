using Microsoft.EntityFrameworkCore;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<ShortenedUrl> ShortLinks => Set<ShortenedUrl>();
    public DbSet<Click> Clicks => Set<Click>();
    public DbSet<ClickAggregate> ClickAggregates => Set<ClickAggregate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
