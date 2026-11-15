using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Errors;
using ShortLink.Api.Models;
using ShortLink.Core;
using ShortLink.Core.Entities;

namespace ShortLink.Api.Controllers;

[ApiController]
[Route("api/links")]
[Authorize]
public sealed class ShortLinksController : ControllerBase
{
    private readonly ShortLinkService _service;

    public ShortLinksController(ShortLinkService service) => _service = service;

    [HttpPost]
    [ProducesResponseType<LinkResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LinkResponse>> Create(CreateLinkRequest request, CancellationToken ct)
    {
        try
        {
            var link = await _service.CreateAsync(CurrentUserId(), request.Url, request.ExpiresAt, ct);
            return CreatedAtAction(nameof(Get), new { id = link.Id }, ToResponse(link));
        }
        catch (Exception ex)
        {
            return ErrorMapper.Result<LinkResponse>(ex);
        }
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LinkResponse>>> List(CancellationToken ct)
    {
        var links = await _service.ListForOwnerAsync(CurrentUserId(), ct);
        return Ok(links.Select(ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<LinkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LinkResponse>> Get(Guid id, CancellationToken ct)
    {
        var link = await _service.GetForOwnerAsync(CurrentUserId(), id, ct);
        return link is null ? NotFound() : Ok(ToResponse(link));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<LinkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LinkResponse>> Update(Guid id, UpdateLinkRequest request, CancellationToken ct)
    {
        try
        {
            var link = await _service.UpdateUrlAsync(CurrentUserId(), id, request.Url, request.ExpiresAt, ct);
            return Ok(ToResponse(link));
        }
        catch (Exception ex)
        {
            return ErrorMapper.Result<LinkResponse>(ex);
        }
    }

    [HttpGet("{id:guid}/analytics")]
    [ProducesResponseType<AnalyticsSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalyticsSummary>> Analytics(Guid id, CancellationToken ct)
    {
        var summary = await _service.GetAnalyticsAsync(CurrentUserId(), id, ct);
        return summary is null ? NotFound() : Ok(summary);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _service.DeleteByOwnerAsync(CurrentUserId(), id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return ErrorMapper.Result(ex);
        }
    }

    private Guid CurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static LinkResponse ToResponse(ShortenedUrl link) => new(
        link.Id,
        link.Slug,
        link.Url,
        link.OwnerId,
        link.CreatedAt,
        link.ExpiresAt,
        link.ClickCount);
}
