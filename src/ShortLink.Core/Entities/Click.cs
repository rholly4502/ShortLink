namespace ShortLink.Core.Entities;

public class Click
{
    public Guid Id { get; set; }
    public Guid ShortenedUrlId { get; set; }
    public ShortenedUrl? ShortenedUrl { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public DeviceType Device { get; set; } = DeviceType.Other;
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public DateTimeOffset ClickedAt { get; set; }
}
