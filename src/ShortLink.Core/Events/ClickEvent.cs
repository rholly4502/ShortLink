using ShortLink.Core.Entities;

namespace ShortLink.Core.Events;

public sealed record ClickEvent(
    Guid ShortenedUrlId,
    DateTimeOffset OccurredAt,
    string? IpAddress,
    string? UserAgent,
    string? Referrer,
    DeviceType Device,
    string? Country,
    string? Region,
    string? City);
