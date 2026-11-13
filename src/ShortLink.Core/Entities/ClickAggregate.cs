namespace ShortLink.Core.Entities;

public class ClickAggregate
{
    public Guid Id { get; set; }
    public Guid ShortenedUrlId { get; set; }
    public ShortenedUrl? ShortenedUrl { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public AggregatePeriod Period { get; set; }
    public DeviceType? Device { get; set; }
    public string? Country { get; set; }
    public long Count { get; set; }
}
