namespace ShortLink.Core.Entities;

public class ShortenedUrl
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;
    public string Url { get; set; } = null!;
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long ClickCount { get; set; }

    public List<Click> Clicks { get; set; } = new();
}
