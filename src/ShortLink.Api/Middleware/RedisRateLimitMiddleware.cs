using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace ShortLink.Api.Middleware;

public sealed class RedisRateLimitMiddleware
{
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
        var (scope, limit, window) = ResolvePolicy(context.Request.Path);

        var clientKey = GetClientKey(context);
        var fullKey = $"rl:{scope}:{clientKey}";

        if (!await _limiter.IsAllowedAsync(fullKey, limit, window, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = window.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
            return;
        }

        await _next(context);
    }

    private (string Scope, int Limit, TimeSpan Window) ResolvePolicy(PathString path)
    {
        if (path.StartsWithSegments("/auth"))
            return ("auth", _options.AuthLimit, _options.AuthWindow);

        if (path.StartsWithSegments("/api"))
            return ("api", _options.ApiLimit, _options.ApiWindow);

        return ("redirect", _options.RedirectLimit, _options.RedirectWindow);
    }

    private static string GetClientKey(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
