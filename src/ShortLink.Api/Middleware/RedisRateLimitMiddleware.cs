using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ShortLink.Api.Errors;

namespace ShortLink.Api.Middleware;

public sealed class RedisRateLimitMiddleware
{
    private const int MaxIpHeaderLength = 45;

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

    private static string GetClientKey(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (TryNormalizeForwardedIp(forwarded, out var forwardedIp))
            return forwardedIp;

        return context.Connection.RemoteIpAddress is { } remoteIp
            ? CanonicalizeIp(remoteIp)
            : "unknown";
    }

    private static bool TryNormalizeForwardedIp(string? headerValue, out string normalizedIp)
    {
        normalizedIp = string.Empty;
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var span = headerValue.AsSpan();
        var commaIndex = span.IndexOf(',');
        if (commaIndex >= 0)
            span = span[..commaIndex];

        span = span.Trim();
        if (span.IsEmpty || span.Length > MaxIpHeaderLength || span.Contains('%'))
            return false;

        if (!IPAddress.TryParse(span, out var ip))
            return false;

        normalizedIp = CanonicalizeIp(ip);
        return true;
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
