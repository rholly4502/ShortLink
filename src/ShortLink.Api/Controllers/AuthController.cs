using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Models;
using ShortLink.Core;
using ShortLink.Core.Auth;

namespace ShortLink.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth) => _auth = auth;

    [HttpPost("register")]
    public async Task<ActionResult<UserResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        try
        {
            var user = await _auth.RegisterAsync(request.Username, request.Email, request.Password, ct);
            return CreatedAtAction(nameof(Register), new UserResponse(user.Id, user.Username, user.Email, user.Role.ToString()));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        try
        {
            var pair = await _auth.LoginAsync(request.Identifier, request.Password, ct);
            return Ok(ToResponse(pair));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Kredensial salah." });
        }
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        try
        {
            var pair = await _auth.RefreshAsync(request.RefreshToken, ct);
            return Ok(ToResponse(pair));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Refresh token tidak valid." });
        }
    }

    private static AuthResponse ToResponse(TokenPair pair) => new(
        pair.AccessToken,
        pair.RefreshToken,
        pair.AccessExpiresAt,
        Guid.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}
