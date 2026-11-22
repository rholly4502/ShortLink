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
    [InlineData("https://[2002:5db8:d822::]/")]
    [InlineData("https://[64:ff9b::93.184.216.34]/")]
    [InlineData("https://example.com./path")]
    [InlineData("https://example.com/profile?email=user@example.com")]
    [InlineData("https://example.com/@handle")]
    [InlineData("https://münchen.de/path")]
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
    [InlineData("http://postgres:5432/")]
    [InlineData("http://redis:6379/")]
    [InlineData("http://rabbitmq:15672/")]
    [InlineData("http://shortlink-api:8080/")]
    [InlineData("http://metadata/")]
    [InlineData("http://intranet/")]
    [InlineData("http://router.home.arpa/")]
    [InlineData("http://home.arpa/")]
    [InlineData("http://box.localdomain/")]
    [InlineData("http://svc.cluster.local/")]
    [InlineData("https://example..com/")]
    [InlineData("https://.example.com/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.1.2.3:8080/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("http://100.64.0.1/cgnat")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://192.0.0.1/")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://198.18.0.1/")]
    [InlineData("http://198.19.255.254/")]
    [InlineData("http://224.0.0.1/multicast")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data/")]
    [InlineData("http://[::127.0.0.1]/")]
    [InlineData("http://[::169.254.169.254]/latest/meta-data/")]
    [InlineData("http://[::10.0.0.1]/")]
    [InlineData("http://[::ffff:0:127.0.0.1]/")]
    [InlineData("http://[::ffff:0:169.254.169.254]/")]
    [InlineData("http://[64:ff9b::127.0.0.1]/")]
    [InlineData("http://[64:ff9b::169.254.169.254]/")]
    [InlineData("http://[64:ff9b:1::10.0.0.1]/")]
    [InlineData("http://[2002:7f00:0001::]/")]
    [InlineData("http://[2002:a9fe:a9fe::]/")]
    [InlineData("http://[2002:0a00:0001::]/")]
    [InlineData("http://[2001:0:4136:e378:8000:63bf:3fff:fdd2]/")]
    [InlineData("http://[100::1]/")]
    [InlineData("http://[fe80::1%25eth0]/")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946%25eth0]/")]
    public void IsValid_ReturnsFalse_ForLoopbackPrivateOrInternalHost(string url)
    {
        Assert.False(_sut.IsValid(url));
        Assert.False(_sut.TryNormalize(url, out _));
    }

    [Theory]
    [InlineData("https://evil.com\\@example.com")]
    [InlineData("https://example.com\\@evil.com")]
    [InlineData("https://example.com\\path")]
    [InlineData("https:\\\\example.com")]
    [InlineData("https://@example.com")]
    [InlineData("http:example.com")]
    [InlineData("https:///example.com")]
    [InlineData("https://%31%32%37.0.0.1/")]
    [InlineData("https://example.com/\r\nSet-Cookie: a=1")]
    [InlineData("https://exa\tmple.com")]
    [InlineData("https://example.com/\0path")]
    [InlineData("http://app\u3002localhost/")]
    [InlineData("http://127\u30020\u30020\u30021/")]
    public void IsValid_ReturnsFalse_ForParserDifferentialOrControlChars(string url)
    {
        Assert.False(_sut.IsValid(url));
        Assert.False(_sut.TryNormalize(url, out _));
    }

    [Fact]
    public void IsValid_EnforcesMaxUrlLengthIncludingCanonicalExpansion()
    {
        const string prefix = "https://example.com/";
        var exactMax = prefix + new string('a', UrlValidator.MaxUrlLength - prefix.Length);
        var tooLongRaw = prefix + new string('a', UrlValidator.MaxUrlLength - prefix.Length + 1);
        var tooLongExpanded = prefix + new string('é', 340);

        Assert.Equal(UrlValidator.MaxUrlLength, exactMax.Length);
        Assert.True(_sut.TryNormalize(exactMax, out var normalized));
        Assert.Equal(UrlValidator.MaxUrlLength, normalized.AbsoluteUri.Length);

        Assert.False(_sut.IsValid(tooLongRaw));
        Assert.True(tooLongExpanded.Length < UrlValidator.MaxUrlLength);
        Assert.False(_sut.IsValid(tooLongExpanded));
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
