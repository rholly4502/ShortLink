using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ShortLink.Core;

namespace ShortLink.Api.Routing;

/// <summary>
/// Route constraint yang mengecualikan path terreservasi di <see cref="SlugValidator.ReservedPaths"/>
/// dari endpoint redirect GET /{slug}, sehingga rute SPA single-segment (mis. /login, /register, /links)
/// dan prefix /api, /auth tidak tertangkap RedirectController maupun menyentuh Redis/PostgreSQL.
/// </summary>
public sealed class NonReservedSlugRouteConstraint : IRouteConstraint
{
    public const string ConstraintName = "nonReservedSlug";

    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (values is null || !values.TryGetValue(routeKey, out var rawValue) || rawValue is null)
            return false;

        var slug = Convert.ToString(rawValue, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(slug))
            return false;

        return !SlugValidator.IsReserved(slug);
    }
}
