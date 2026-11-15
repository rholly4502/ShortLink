using System.ComponentModel.DataAnnotations;

namespace ShortLink.Api.Models;

public sealed record RegisterRequest(
    [property: Required, StringLength(50, MinimumLength = 3)] string Username,
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 8)] string Password);

public sealed record LoginRequest(
    [property: Required, StringLength(254)] string Identifier,
    [property: Required] string Password);

public sealed record RefreshRequest(
    [property: Required] string RefreshToken);

public sealed record UserResponse(Guid Id, string Username, string Email, string Role);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Username,
    string Email,
    string Role);
