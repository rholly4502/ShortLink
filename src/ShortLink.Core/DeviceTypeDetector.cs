using ShortLink.Core.Entities;

namespace ShortLink.Core;

/// <summary>Deteksi tipe perangkat dari User-Agent (heuristik sederhana).</summary>
public sealed class DeviceTypeDetector
{
    public static DeviceType Detect(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return DeviceType.Other;

        var ua = userAgent.ToLowerInvariant();

        if (ua.Contains("ipad") || ua.Contains("tablet"))
            return DeviceType.Tablet;

        if (ua.Contains("iphone") || ua.Contains("mobile"))
            return DeviceType.Mobile;

        if (ua.Contains("android"))
            return DeviceType.Tablet;

        return DeviceType.Desktop;
    }
}
