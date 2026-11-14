using ShortLink.Core;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

public class DeviceTypeDetectorTests
{
    [Fact]
    public void Detect_Null_ReturnsOther() => Assert.Equal(DeviceType.Other, DeviceTypeDetector.Detect(null));

    [Fact]
    public void Detect_Empty_ReturnsOther() => Assert.Equal(DeviceType.Other, DeviceTypeDetector.Detect(""));

    [Fact]
    public void Detect_Iphone_ReturnsMobile()
        => Assert.Equal(DeviceType.Mobile, DeviceTypeDetector.Detect("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15"));

    [Fact]
    public void Detect_AndroidMobile_ReturnsMobile()
        => Assert.Equal(DeviceType.Mobile, DeviceTypeDetector.Detect("Mozilla/5.0 (Linux; Android 14; Pixel 8) Mobile"));

    [Fact]
    public void Detect_Ipad_ReturnsTablet()
        => Assert.Equal(DeviceType.Tablet, DeviceTypeDetector.Detect("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)"));

    [Fact]
    public void Detect_AndroidTablet_ReturnsTablet()
        => Assert.Equal(DeviceType.Tablet, DeviceTypeDetector.Detect("Mozilla/5.0 (Linux; Android 14; SM-X200) AppleWebKit/537.36"));

    [Fact]
    public void Detect_DesktopChrome_ReturnsDesktop()
        => Assert.Equal(DeviceType.Desktop, DeviceTypeDetector.Detect("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0"));
}
