namespace ShortLink.Core.Auth;

/// <summary>Sepasang token akses + refresh.</summary>
public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);
