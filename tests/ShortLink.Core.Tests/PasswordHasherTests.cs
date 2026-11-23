using ShortLink.Core.Abstractions;
using ShortLink.Infrastructure.Security;

namespace ShortLink.Core.Tests;

public sealed class PasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new BcryptPasswordHasher();

    [Fact]
    public void Hash_Verify_ReturnsTrue_ForCorrectPassword()
    {
        var hash = _hasher.Hash("s3cret!");
        Assert.True(_hasher.Verify(hash, "s3cret!"));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongPassword()
    {
        var hash = _hasher.Hash("s3cret!");
        Assert.False(_hasher.Verify(hash, "wrong"));
    }

    [Fact]
    public void Hash_ProducesDifferentSalts()
    {
        Assert.NotEqual(_hasher.Hash("same"), _hasher.Hash("same"));
    }

    [Fact]
    public void Hash_Throws_WhenPasswordExceeds72Utf8Bytes()
    {
        var valid72 = new string('a', 72);
        var hash = _hasher.Hash(valid72);
        Assert.True(_hasher.Verify(hash, valid72));

        Assert.Throws<ArgumentException>(() => _hasher.Hash(new string('a', 73)));
        Assert.Throws<ArgumentException>(() => _hasher.Hash(new string('€', 25)));
    }

    [Fact]
    public void Verify_Rejects73BytePrefixExtensionAndMalformedHashGracefully()
    {
        var valid72 = new string('a', 72);
        var hash = _hasher.Hash(valid72);

        // Cegah silent truncation BCrypt di mana karakter ke-73+ diabaikan
        Assert.False(_hasher.Verify(hash, valid72 + "EXTRA"));
        Assert.False(_hasher.Verify("not-a-valid-bcrypt-hash", "s3cret!"));
        Assert.False(_hasher.Verify("", "s3cret!"));
    }
}
