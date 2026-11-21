namespace ShortLink.Api.Middleware;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int AuthLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;
    public int ApiLimit { get; set; } = 60;
    public int ApiWindowSeconds { get; set; } = 60;
    public int RedirectLimit { get; set; } = 30;
    public int RedirectWindowSeconds { get; set; } = 60;

    public TimeSpan AuthWindow => TimeSpan.FromSeconds(AuthWindowSeconds);
    public TimeSpan ApiWindow => TimeSpan.FromSeconds(ApiWindowSeconds);
    public TimeSpan RedirectWindow => TimeSpan.FromSeconds(RedirectWindowSeconds);
}
