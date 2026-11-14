using ShortLink.Core.Auth;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Abstractions;

/// <summary>Abstraksi penerbitan/validasi JWT.</summary>
public interface IAuthTokenService
{
    TokenPair Generate(User user);
    Guid? ValidateRefreshToken(string refreshToken);
}
