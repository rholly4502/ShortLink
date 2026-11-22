using System.Net;
using System.Net.Sockets;
using ShortLink.Core.Abstractions;

namespace ShortLink.Core;

/// <summary>Validasi URL: hanya http/https absolute, cegah open-redirect/trik userinfo dan target SSRF internal.</summary>
public sealed class UrlValidator : IUrlValidator
{
    public const int MaxUrlLength = 2048;

    private static readonly string[] RestrictedHostSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".localdomain",
        ".home.arpa"
    ];

    public bool IsValid(string url) => TryNormalize(url, out _);

    public bool TryNormalize(string input, out Uri normalized)
    {
        normalized = null!;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxUrlLength)
            return false;

        foreach (var ch in trimmed)
        {
            if (ch <= 0x1F || ch == 0x7F)
                return false;
        }

        // Validasi authority mentah sebelum Uri.TryCreate agar parser differential
        // (seperti backslash authority, empty userinfo '@', percent-encoded host, atau zone ID) ditolak.
        var schemeSeparator = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator <= 0)
            return false;

        var rawScheme = trimmed.AsSpan(0, schemeSeparator);
        if (!rawScheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !rawScheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var afterScheme = trimmed.AsSpan(schemeSeparator + 3);
        var authorityEnd = afterScheme.IndexOfAny('/', '?', '#');
        var rawAuthority = authorityEnd >= 0 ? afterScheme[..authorityEnd] : afterScheme;
        if (rawAuthority.IsEmpty || rawAuthority.IndexOfAny('\\', '@', '%') >= 0)
            return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Cegah open-redirect/penyalahgunaan: tolak URL berisi kredensial
        // (mis. https://evil.com@trusted.com) — bisa menipu pengguna.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (uri.AbsoluteUri.Length > MaxUrlLength)
            return false;

        // Cegah SSRF / redirect ke host internal, loopback, private network, atau cloud metadata.
        if (IsRestrictedHost(uri))
            return false;

        normalized = uri;
        return true;
    }

    private static bool IsRestrictedHost(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
            return true;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(host, out _))
            return !IPAddress.TryParse(host, out var ip) || IsRestrictedIpAddress(ip);

        if (!host.Contains('.') || host.StartsWith('.') || host.Contains("..", StringComparison.Ordinal))
            return true;

        if (host.Equals("home.arpa", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var suffix in RestrictedHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsRestrictedIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return IsRestrictedIpv4(ip.MapToIPv4().GetAddressBytes());

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return IsRestrictedIpv4(ip.GetAddressBytes());

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;

            ReadOnlySpan<byte> b = ip.GetAddressBytes();

            // fc00::/7 (Unique Local Address)
            if ((b[0] & 0xFE) == 0xFC)
                return true;

            // 100::/64 (Discard-Only, RFC 6666)
            if (b[0] == 0x01 && b[1] == 0x00 && IsAllZero(b.Slice(2, 6)))
                return true;

            // ::/96 (deprecated IPv4-compatible, RFC 4291)
            if (IsAllZero(b[..12]))
                return true;

            // ::ffff:0:0:0/96 (IPv4-translated, RFC 2765)
            if (IsAllZero(b[..8]) && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0)
                return IsRestrictedIpv4(b.Slice(12, 4));

            // 64:ff9b::/96 (NAT64 well-known, RFC 6052)
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && IsAllZero(b.Slice(4, 8)))
                return IsRestrictedIpv4(b.Slice(12, 4));

            // 64:ff9b:1::/48 (NAT64 local-use, RFC 8215)
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
                return true;

            // 2002::/16 (6to4, RFC 3056)
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsRestrictedIpv4(b.Slice(2, 4));

            // 2001::/32 (Teredo, RFC 4380)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
                return true;

            return false;
        }

        return true;
    }

    private static bool IsRestrictedIpv4(ReadOnlySpan<byte> b)
        => b[0] == 0                                        // 0.0.0.0/8 (this network)
           || b[0] == 10                                    // 10.0.0.0/8 (RFC 1918)
           || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64.0.0/10 (CGNAT)
           || b[0] == 127                                   // 127.0.0.0/8 (loopback)
           || (b[0] == 169 && b[1] == 254)                  // 169.254.0.0/16 (link-local / cloud IMDS)
           || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // 172.16.0.0/12 (RFC 1918)
           || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 (IETF protocol assignments)
           || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16 (RFC 1918)
           || (b[0] == 198 && (b[1] == 18 || b[1] == 19))   // 198.18.0.0/15 (benchmarking)
           || b[0] >= 224;                                  // 224.0.0.0/4 multicast & 240.0.0.0/4 reserved

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (var value in span)
        {
            if (value != 0)
                return false;
        }

        return true;
    }
}
