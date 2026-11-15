using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Auth;
using ShortLink.Core.Entities;

namespace ShortLink.Infrastructure.Security;

public sealed class JwtTokenService : IAuthTokenService
{
    private const string TokenTypeClaim = "token_type";
    private const string RefreshTokenType = "refresh";
    private const string AccessTokenType = "access";

    private readonly JwtOptions _o;

    public JwtTokenService(IOptions<JwtOptions> options) => _o = options.Value;

    public TokenPair Generate(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExp = now.AddMinutes(_o.AccessTokenTtlMinutes);
        var refreshExp = now.AddDays(_o.RefreshTokenTtlDays);

        var access = WriteToken(user, AccessTokenType, now, accessExp);
        var refresh = WriteToken(user, RefreshTokenType, now, refreshExp);

        return new TokenPair(access, refresh, accessExp, refreshExp, user.Id, user.Username, user.Email, user.Role.ToString());
    }

    public Guid? ValidateRefreshToken(string refreshToken)
    {
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(refreshToken, NewValidationParameters(), out var validated);
            if (validated is not JwtSecurityToken jwt || jwt.Payload[TokenTypeClaim]?.ToString() != RefreshTokenType)
                return null;

            var sub = jwt.Payload[JwtRegisteredClaimNames.Sub]?.ToString();
            return Guid.TryParse(sub, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    private TokenValidationParameters NewValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _o.Issuer,
        ValidateAudience = true,
        ValidAudience = _o.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(),
        ClockSkew = TimeSpan.Zero
    };

    private SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(_o.SigningKey));

    private string WriteToken(User user, string type, DateTimeOffset now, DateTimeOffset expires)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(TokenTypeClaim, type)
        };

        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
