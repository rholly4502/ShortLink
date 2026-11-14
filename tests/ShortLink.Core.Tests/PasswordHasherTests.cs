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
}
