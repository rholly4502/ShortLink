using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShortLink.Core;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Persistence;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).IsRequired().HasMaxLength(50);
        b.Property(x => x.Email).IsRequired().HasMaxLength(256);
        b.Property(x => x.PasswordHash).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.Username).IsUnique();
        b.HasIndex(x => x.Email).IsUnique();
        b.HasMany(x => x.ShortLinks)
         .WithOne(x => x.Owner)
         .HasForeignKey(x => x.OwnerId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ShortenedUrlConfiguration : IEntityTypeConfiguration<ShortenedUrl>
{
    public void Configure(EntityTypeBuilder<ShortenedUrl> b)
    {
        b.ToTable("short_links");
        b.HasKey(x => x.Id);
        b.Property(x => x.Slug).IsRequired().HasMaxLength(32);
        b.Property(x => x.Url).IsRequired().HasMaxLength(2048);
        b.Property(x => x.OwnerId).IsRequired();
        b.Property(x => x.ClickCount).HasDefaultValue(0L);
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasMany(x => x.Clicks)
         .WithOne(x => x.ShortenedUrl)
         .HasForeignKey(x => x.ShortenedUrlId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ClickConfiguration : IEntityTypeConfiguration<Click>
{
    public void Configure(EntityTypeBuilder<Click> b)
    {
        b.ToTable("clicks");
        b.HasKey(x => x.Id);
        b.Property(x => x.IpAddress).HasMaxLength(ClickIngestionService.MaxIpAddressLength);
        b.Property(x => x.UserAgent).HasMaxLength(ClickIngestionService.MaxUserAgentLength);
        b.Property(x => x.Referrer).HasMaxLength(ClickIngestionService.MaxReferrerLength);
        b.Property(x => x.Country).HasMaxLength(ClickIngestionService.MaxCountryLength);
        b.Property(x => x.Region).HasMaxLength(ClickIngestionService.MaxRegionLength);
        b.Property(x => x.City).HasMaxLength(ClickIngestionService.MaxCityLength);
        b.Property(x => x.ClickedAt).IsRequired();
        b.HasIndex(x => new { x.ShortenedUrlId, x.ClickedAt });
    }
}

public sealed class ClickAggregateConfiguration : IEntityTypeConfiguration<ClickAggregate>
{
    public void Configure(EntityTypeBuilder<ClickAggregate> b)
    {
        b.ToTable("click_aggregates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Count).HasDefaultValue(0L);
        b.HasIndex(x => new { x.ShortenedUrlId, x.PeriodStart, x.Period, x.Device, x.Country }).IsUnique();
    }
}
