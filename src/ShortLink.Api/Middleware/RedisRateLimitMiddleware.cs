using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ShortLink.Api.Errors;

namespace ShortLink.Api.Middleware;

public sealed class RedisRateLimitMiddleware
{
    public const int MaxIpHeaderLength = 45;

    private static readonly ReadOnlyMemory<byte> TooManyRequestsBody = JsonSerializer.SerializeToUtf8Bytes(
        new ApiError("Terlalu banyak permintaan. Coba lagi nanti."),
        JsonSerializerOptions.Web);

    private readonly RequestDelegate _next;
    private readonly FixedWindowRateLimiter _limiter;
    private readonly RateLimitOptions _options;

    public RedisRateLimitMiddleware(RequestDelegate next, FixedWindowRateLimiter limiter, RateLimitOptions options)
    {
        _next = next;
        _limiter = limiter;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthProbe(context.Request))
        {
            await _next(context);
            return;
        }

        var (scope, limit, window) = ResolvePolicy(context.Request);

        var clientKey = GetClientKey(context);
        var fullKey = $"rl:{scope}:{clientKey}";

        if (!await _limiter.IsAllowedAsync(fullKey, limit, window, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = window.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.Body.WriteAsync(TooManyRequestsBody, context.RequestAborted);
            }

            return;
        }

        await _next(context);
    }

    private static bool IsHealthProbe(HttpRequest request)
        => (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
           && NormalizePath(request.Path).Equals("/api/health", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkCreateRequest(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
           && NormalizePath(request.Path).Equals("/api/links", StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<char> NormalizePath(PathString path)
    {
        var span = (path.Value ?? string.Empty).AsSpan();
        while (span.Length > 1 && span[^1] == '/')
        {
            span = span[..^1];
        }

        return span;
    }

    private (string Scope, int Limit, TimeSpan Window) ResolvePolicy(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
            return ("auth", _options.AuthLimit, _options.AuthWindow);

        if (IsLinkCreateRequest(request))
            return ("create", _options.CreateLimit, _options.CreateWindow);

        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return ("api", _options.ApiLimit, _options.ApiWindow);

        return ("redirect", _options.RedirectLimit, _options.RedirectWindow);
    }

    private string GetClientKey(HttpContext context)
    {
        if (TryExtractClientIp(context, _options.TrustForwardedHeaders, out var ip))
            return ip;

        return "unknown";
    }

    public static string? ResolveClientIp(HttpContext context, bool trustForwardedHeaders = true)
        => TryExtractClientIp(context, trustForwardedHeaders, out var ip) ? ip : null;

    public static bool TryExtractClientIp(HttpContext context, bool trustForwardedHeaders, out string normalizedIp)
    {
        ArgumentNullException.ThrowIfNull(context);
        normalizedIp = string.Empty;

        var remoteIp = context.Connection.RemoteIpAddress;
        var canTrustForwarded = trustForwardedHeaders && (remoteIp is null || IsTrustedProxyIp(remoteIp));

        if (canTrustForwarded)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].LastOrDefault();
            if (TryNormalizeForwardedIp(forwarded, out normalizedIp))
                return true;
        }

        if (remoteIp is not null)
        {
            normalizedIp = CanonicalizeIp(remoteIp);
            return true;
        }

        return false;
    }

    public static bool IsTrustedProxyIp(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
            return IsTrustedProxyIpv4(ip.MapToIPv4().GetAddressBytes());

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return IsTrustedProxyIpv4(ip.GetAddressBytes());

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                return true;

            ReadOnlySpan<byte> b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 Unique Local Address
        }

        return false;
    }

    private static bool IsTrustedProxyIpv4(ReadOnlySpan<byte> b)
        => b[0] == 127                                   // 127.0.0.0/8 loopback
           || b[0] == 10                                 // 10.0.0.0/8 RFC 1918
           || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16.0.0/12 RFC 1918 (includes Docker bridge)
           || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16 RFC 1918
           || (b[0] == 100 && (b[1] & 0xC0) == 64)       // 100.64.0.0/10 CGNAT / service mesh
           || (b[0] == 169 && b[1] == 254);              // 169.254.0.0/16 link-local

    public static bool TryNormalizeForwardedIp(string? headerValue, out string normalizedIp)
    {
        normalizedIp = string.Empty;
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var span = headerValue.AsSpan();
        var lastComma = span.LastIndexOf(',');
        if (lastComma >= 0)
            span = span[(lastComma + 1)..];

        span = span.Trim();
        if (span.IsEmpty || span.Length > MaxIpHeaderLength)
            return false;

        foreach (var ch in span)
        {
            if (!IsValidIpTokenChar(ch))
                return false;
        }

        if (!IPAddress.TryParse(span, out var ip))
            return false;

        if (!IsStrictCanonicalIpToken(span, ip))
            return false;

        normalizedIp = CanonicalizeIp(ip);
        return true;
    }

    private static bool IsValidIpTokenChar(char ch)
        => char.IsAsciiHexDigit(ch) || ch is '.' or ':';

    private static bool IsStrictCanonicalIpToken(ReadOnlySpan<char> token, IPAddress parsed)
    {
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            // Tolak bentuk non-standar IPv4 (decimal integer, hex, octal leading zero, atau shorthand < 4 oktet)
            // agar penyerang tidak dapat memalsukan atau mem-bypass rate limit dengan variasi format IPv4.
            var dotCount = 0;
            var segmentLen = 0;
            var leadingZero = false;

            foreach (var ch in token)
            {
                if (ch == '.')
                {
                    if (segmentLen == 0)
                        return false;
                    dotCount++;
                    segmentLen = 0;
                    leadingZero = false;
                    continue;
                }

                if (!char.IsAsciiDigit(ch))
                    return false;

                if (segmentLen == 0 && ch == '0')
                    leadingZero = true;
                else if (leadingZero)
                    return false;

                segmentLen++;
                if (segmentLen > 3)
                    return false;
            }

            return dotCount == 3 && segmentLen > 0;
        }

        return parsed.AddressFamily == AddressFamily.InterNetworkV6 && token.Contains(':');
    }

    private static string CanonicalizeIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
            ip = new IPAddress(ip.GetAddressBytes());

        return ip.ToString();
    }
}
