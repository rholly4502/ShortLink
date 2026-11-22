using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ShortLink.Api.Routing;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;

namespace ShortLink.Api.Controllers;

/// <summary>Endpoint publik: GET /{slug} → 302 ke URL asli; sertai event klik.</summary>
[ApiController]
public sealed class RedirectController : ControllerBase
{
    private readonly CachedSlugResolver _resolver;
    private readonly IClickEventPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger<RedirectController>? _logger;
    private readonly IUrlValidator _urlValidator;

    public RedirectController(
        CachedSlugResolver resolver,
        IClickEventPublisher publisher,
        IClock clock,
        ILogger<RedirectController>? logger = null,
        IUrlValidator? urlValidator = null)
    {
        _resolver = resolver;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
        _urlValidator = urlValidator ?? new UrlValidator();
    }

    [HttpGet("{slug:regex(^[[a-zA-Z0-9]]+$):" + NonReservedSlugRouteConstraint.ConstraintName + "}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RedirectTo(string slug, CancellationToken ct)
    {
        var link = await _resolver.ResolveAsync(slug, ct);
        if (link is null)
            return NotFound();

        if (!_urlValidator.TryNormalize(link.Url, out var safeTarget))
        {
            _logger?.LogWarning(
                "Blocked unsafe redirect target for link '{LinkId}' (slug '{Slug}').",
                link.Id,
                slug);
            return NotFound();
        }

        var userAgent = Request.Headers.UserAgent.ToString();
        var clickEvent = new ClickEvent(
            link.Id,
            _clock.UtcNow,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent,
            Request.Headers.Referer.ToString(),
            DeviceTypeDetector.Detect(userAgent),
            Country: null,
            Region: null,
            City: null);

        await TryPublishClickAsync(clickEvent, slug, ct);

        return Redirect(safeTarget.AbsoluteUri);
    }

    private async Task TryPublishClickAsync(ClickEvent clickEvent, string slug, CancellationToken ct)
    {
        try
        {
            await _publisher.PublishAsync(clickEvent, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Best-effort click event publish failed for link '{LinkId}' (slug '{Slug}'); proceeding with redirect.",
                clickEvent.ShortenedUrlId,
                slug);
        }
    }
}
