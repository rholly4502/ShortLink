using System.ComponentModel.DataAnnotations;
using ShortLink.Core;

namespace ShortLink.Api.Models;

public sealed record CreateLinkRequest(
    [Required, StringLength(UrlValidator.MaxUrlLength)] string Url,
    DateTimeOffset? ExpiresAt,
    [StringLength(32)] string? CustomSlug = null);

public sealed record UpdateLinkRequest(
    [Required, StringLength(UrlValidator.MaxUrlLength)] string Url,
    DateTimeOffset? ExpiresAt);

public sealed record LinkResponse(
    Guid Id,
    string Slug,
    string Url,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long ClickCount);
