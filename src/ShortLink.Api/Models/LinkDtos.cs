using System.ComponentModel.DataAnnotations;

namespace ShortLink.Api.Models;

public sealed record CreateLinkRequest(
    [Required, StringLength(2048)] string Url,
    DateTimeOffset? ExpiresAt,
    [StringLength(32)] string? CustomSlug = null);

public sealed record UpdateLinkRequest(
    [Required, StringLength(2048)] string Url,
    DateTimeOffset? ExpiresAt);

public sealed record LinkResponse(
    Guid Id,
    string Slug,
    string Url,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long ClickCount);
