using Microsoft.AspNetCore.Mvc;
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

    public RedirectController(CachedSlugResolver resolver, IClickEventPublisher publisher, IClock clock)
    {
        _resolver = resolver;
        _publisher = publisher;
        _clock = clock;
    }

    [HttpGet("{slug:regex(^[[a-zA-Z0-9]]+$)}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RedirectTo(string slug, CancellationToken ct)
    {
        var link = await _resolver.ResolveAsync(slug, ct);
        if (link is null)
            return NotFound();

        var userAgent = Request.Headers.UserAgent.ToString();

        await _publisher.PublishAsync(new ClickEvent(
            link.Id,
            _clock.UtcNow,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent,
            Request.Headers.Referer.ToString(),
            DeviceTypeDetector.Detect(userAgent),
            Country: null,
            Region: null,
            City: null), ct);

        return Redirect(link.Url);
    }
}
