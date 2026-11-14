namespace ShortLink.Api.Models;

public sealed record CreateLinkRequest(string Url, DateTimeOffset? ExpiresAt);
public sealed record UpdateLinkRequest(string Url, DateTimeOffset? ExpiresAt);
public sealed record LinkResponse(
    Guid Id,
    string Slug,
    string Url,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long ClickCount);
