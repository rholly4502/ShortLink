using ShortLink.Core.Abstractions;

namespace ShortLink.Infrastructure.Security;

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public bool Verify(string hashed, string password) => BCrypt.Net.BCrypt.Verify(password, hashed);
}
