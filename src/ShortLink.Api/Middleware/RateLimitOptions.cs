namespace ShortLink.Api.Middleware;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int AuthLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;
    public int CreateLimit { get; set; } = 20;
    public int CreateWindowSeconds { get; set; } = 60;
    public int ApiLimit { get; set; } = 60;
    public int ApiWindowSeconds { get; set; } = 60;
    public int RedirectLimit { get; set; } = 30;
    public int RedirectWindowSeconds { get; set; } = 60;

    public TimeSpan AuthWindow => TimeSpan.FromSeconds(Math.Max(1, AuthWindowSeconds));
    public TimeSpan CreateWindow => TimeSpan.FromSeconds(Math.Max(1, CreateWindowSeconds));
    public TimeSpan ApiWindow => TimeSpan.FromSeconds(Math.Max(1, ApiWindowSeconds));
    public TimeSpan RedirectWindow => TimeSpan.FromSeconds(Math.Max(1, RedirectWindowSeconds));
}
