using ShortLink.Core;
using ShortLink.Core.Abstractions;

namespace ShortLink.Core.Tests;

public class UrlValidatorTests
{
    private readonly IUrlValidator _sut = new UrlValidator();

    // --- IsValid ---

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path/to/page?q=1&x=2#frag")]
    [InlineData("https://sub.example.co.uk:8443/deep/path")]
    public void IsValid_ReturnsTrue_ForAbsoluteHttpOrHttpsUrl(string url)
    {
        Assert.True(_sut.IsValid(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://files.example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:user@example.com")]
    [InlineData("https://evil.com@example.com")] // userinfo / credential trick (open-redirect guard)
    public void IsValid_ReturnsFalse_ForInvalidOrUnsafeUrl(string? url)
    {
        Assert.False(_sut.IsValid(url!));
    }

    // --- TryNormalize ---

    [Fact]
    public void TryNormalize_ReturnsTrue_AndNormalizedUri_ForValidUrl()
    {
        var ok = _sut.TryNormalize("HTTPS://Example.COM:443/Path", out var uri);

        Assert.True(ok);
        Assert.NotNull(uri);
        Assert.True(uri!.IsAbsoluteUri);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("example.com", uri.Host);
    }

    [Fact]
    public void TryNormalize_ReturnsFalse_ForUnsupportedScheme()
    {
        Assert.False(_sut.TryNormalize("javascript:alert(1)", out _));
        Assert.False(_sut.TryNormalize("ftp://x.com", out _));
    }

    [Fact]
    public void TryNormalize_ReturnsFalse_ForUserInfo()
    {
        Assert.False(_sut.TryNormalize("https://evil@example.com", out _));
    }

    [Fact]
    public void TryNormalize_ReturnsFalse_ForNullOrWhitespace()
    {
        Assert.False(_sut.TryNormalize(null!, out _));
        Assert.False(_sut.TryNormalize("   ", out _));
    }
}
