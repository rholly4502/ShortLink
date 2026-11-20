using System.Net;
using System.Net.Sockets;
using ShortLink.Core.Abstractions;

namespace ShortLink.Core;

/// <summary>Validasi URL: hanya http/https absolute, cegah open-redirect/trik userinfo dan target SSRF internal.</summary>
public sealed class UrlValidator : IUrlValidator
{
    public bool IsValid(string url) => TryNormalize(url, out _);

    public bool TryNormalize(string input, out Uri normalized)
    {
        normalized = null!;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Cegah open-redirect/penyalahgunaan: tolak URL berisi kredensial
        // (mis. https://evil.com@trusted.com) — bisa menipu pengguna.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        // Cegah SSRF / redirect ke host internal, loopback, private network, atau cloud metadata.
        if (IsRestrictedHost(uri))
            return false;

        normalized = uri;
        return true;
    }

    private static bool IsRestrictedHost(Uri uri)
    {
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
            return true;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
            return IsRestrictedIpAddress(ip);

        return false;
    }

    private static bool IsRestrictedIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                       // 0.0.0.0/8 (this network)
                || b[0] == 10                                      // 10.0.0.0/8 (RFC 1918)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // 100.64.0.0/10 (CGNAT)
                || b[0] == 127                                     // 127.0.0.0/8 (loopback)
                || (b[0] == 169 && b[1] == 254)                    // 169.254.0.0/16 (link-local / cloud IMDS)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)       // 172.16.0.0/12 (RFC 1918)
                || (b[0] == 192 && b[1] == 168)                    // 192.168.0.0/16 (RFC 1918)
                || b[0] >= 224;                                    // 224.0.0.0/4 multicast & 240.0.0.0/4 reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;

            var b = ip.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc;                          // fc00::/7 (Unique Local Address)
        }

        return false;
    }
}
