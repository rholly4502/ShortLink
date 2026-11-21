using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Api.Security;

/// <summary>Validasi tambahan untuk Bearer JWT agar hanya access token dengan subjek GUID valid yang diterima.</summary>
public static class JwtBearerValidation
{
    public static Task ValidateAccessTokenAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            context.Fail("Missing principal.");
            return Task.CompletedTask;
        }

        var tokenTypes = principal.FindAll(JwtTokenService.TokenTypeClaim)
            .Select(c => c.Value)
            .ToList();
        if (tokenTypes.Count != 1 || !string.Equals(tokenTypes[0], JwtTokenService.AccessTokenType, StringComparison.Ordinal))
        {
            context.Fail("Access token required.");
            return Task.CompletedTask;
        }

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty)
        {
            context.Fail("Invalid token subject.");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
