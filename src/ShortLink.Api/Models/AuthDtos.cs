using System.ComponentModel.DataAnnotations;
using ShortLink.Core;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Api.Models;

public sealed record RegisterRequest(
    [Required, StringLength(AuthService.MaxUsernameLength, MinimumLength = 3)] string Username,
    [Required, EmailAddress, StringLength(AuthService.MaxEmailLength)] string Email,
    [Required, StringLength(AuthService.MaxPasswordBytes, MinimumLength = 8)] string Password);

public sealed record LoginRequest(
    [Required, StringLength(AuthService.MaxEmailLength)] string Identifier,
    [Required, StringLength(AuthService.MaxPasswordBytes)] string Password);

public sealed record RefreshRequest(
    [Required, StringLength(JwtTokenService.MaxTokenLength)] string RefreshToken);

public sealed record UserResponse(Guid Id, string Username, string Email, string Role);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Username,
    string Email,
    string Role);
