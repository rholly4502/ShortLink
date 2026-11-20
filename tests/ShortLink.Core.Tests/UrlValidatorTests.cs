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
    [InlineData("https://93.184.216.34/public-ip")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/public-ipv6")]
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

    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("https://localhost:8080/api/links")]
    [InlineData("http://localhost./secret")]
    [InlineData("http://app.localhost/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.1.2.3:8080/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("http://100.64.0.1/cgnat")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://224.0.0.1/multicast")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data/")]
    public void IsValid_ReturnsFalse_ForLoopbackPrivateOrInternalHost(string url)
    {
        Assert.False(_sut.IsValid(url));
        Assert.False(_sut.TryNormalize(url, out _));
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
