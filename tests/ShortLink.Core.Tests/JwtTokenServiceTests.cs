using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ShortLink.Core.Entities;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

public sealed class JwtTokenServiceTests
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Issuer = "shortlink";
    private const string Audience = "shortlink-api";

    private static JwtTokenService Create() => new(Microsoft.Extensions.Options.Options.Create(Options()));

    private static JwtOptions Options() => new()
    {
        Issuer = Issuer,
        Audience = Audience,
        SigningKey = Key,
        AccessTokenTtlMinutes = 15,
        RefreshTokenTtlDays = 7
    };

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "alice",
        Email = "alice@x.com",
        PasswordHash = "h",
        Role = UserRole.Admin,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Generate_AccessTokenCarriesSubUsernameAndRoleClaims()
    {
        var user = NewUser();
        var token = Create().Generate(user).AccessToken;

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal("alice", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.UniqueName).Value);
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Admin");
        Assert.Equal(Issuer, jwt.Issuer);
        Assert.Contains(jwt.Audiences, a => a == Audience);
    }

    [Fact]
    public void Generate_AccessTokenExpiryRespectsConfiguredTtl()
    {
        var pair = Create().Generate(NewUser());
        var expected = DateTimeOffset.UtcNow.AddMinutes(15);
        Assert.InRange(pair.AccessExpiresAt, expected.AddSeconds(-5), expected.AddSeconds(5));
    }

    [Fact]
    public void ValidateRefreshToken_ReturnsUserId_ForValidRefreshToken()
    {
        var svc = Create();
        var pair = svc.Generate(NewUser());

        Assert.NotNull(svc.ValidateRefreshToken(pair.RefreshToken));
    }

    [Fact]
    public void ValidateRefreshToken_ReturnsNull_WhenAccessTokenPassed()
    {
        var svc = Create();
        var pair = svc.Generate(NewUser());

        Assert.Null(svc.ValidateRefreshToken(pair.AccessToken));
    }

    [Fact]
    public void ValidateRefreshToken_ReturnsNull_WhenTampered()
    {
        var svc = Create();
        var pair = svc.Generate(NewUser());
        var tampered = pair.RefreshToken.TrimEnd('.') + ".AAAA";

        Assert.Null(svc.ValidateRefreshToken(tampered));
    }

    [Fact]
    public void ValidateRefreshToken_ReturnsNull_WhenSignedWithWrongKey()
    {
        var other = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            Issuer = Issuer, Audience = Audience,
            SigningKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
            AccessTokenTtlMinutes = 15, RefreshTokenTtlDays = 7
        }));
        var pair = other.Generate(NewUser());

        Assert.Null(Create().ValidateRefreshToken(pair.RefreshToken));
    }
}
