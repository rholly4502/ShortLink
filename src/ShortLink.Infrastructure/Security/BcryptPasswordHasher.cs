using System.Text;
using ShortLink.Core;
using ShortLink.Core.Abstractions;

namespace ShortLink.Infrastructure.Security;

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || Encoding.UTF8.GetByteCount(password) > AuthService.MaxPasswordBytes)
            throw new ArgumentException("Password tidak valid atau melebihi batas 72 byte UTF-8.", nameof(password));

        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    public bool Verify(string hashed, string password)
    {
        if (string.IsNullOrWhiteSpace(hashed)
            || string.IsNullOrEmpty(password)
            || Encoding.UTF8.GetByteCount(password) > AuthService.MaxPasswordBytes)
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hashed);
        }
        catch
        {
            return false;
        }
    }
}
