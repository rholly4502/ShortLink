using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Core.Abstractions;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Api.Security;

/// <summary>Validasi tambahan untuk Bearer JWT agar hanya access token dengan subjek GUID valid dan user terdaftar yang diterima.</summary>
public static class JwtBearerValidation
{
    public static async Task ValidateAccessTokenAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            context.Fail("Missing principal.");
            return;
        }

        var tokenTypes = principal.FindAll(JwtTokenService.TokenTypeClaim)
            .Select(c => c.Value)
            .ToList();
        if (tokenTypes.Count != 1 || !string.Equals(tokenTypes[0], JwtTokenService.AccessTokenType, StringComparison.Ordinal))
        {
            context.Fail("Access token required.");
            return;
        }

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty)
        {
            context.Fail("Invalid token subject.");
            return;
        }

        var users = context.HttpContext.RequestServices.GetService<IUserRepository>();
        if (users is not null)
        {
            var user = await users.GetByIdAsync(userId, context.HttpContext.RequestAborted);
            if (user is null)
            {
                context.Fail("Token subject user does not exist.");
                return;
            }
        }
    }
}
