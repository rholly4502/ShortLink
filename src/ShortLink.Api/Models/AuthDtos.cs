using System.ComponentModel.DataAnnotations;

namespace ShortLink.Api.Models;

public sealed record RegisterRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password);

public sealed record LoginRequest(
    [Required, StringLength(254)] string Identifier,
    [Required] string Password);

public sealed record RefreshRequest(
    [Required] string RefreshToken);

public sealed record UserResponse(Guid Id, string Username, string Email, string Role);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Username,
    string Email,
    string Role);
