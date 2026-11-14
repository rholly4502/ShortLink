namespace ShortLink.Core.Abstractions;

/// <summary>Abstraksi hashing password (BCrypt).</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hashed, string password);
}
