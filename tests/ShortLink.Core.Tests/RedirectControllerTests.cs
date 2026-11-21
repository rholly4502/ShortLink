using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using ShortLink.Api.Controllers;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;
using ShortLink.Core.Events;
using ShortLink.Infrastructure.Caching;

namespace ShortLink.Core.Tests;

public class RedirectControllerTests
{
    private readonly FixedClock _clock = new();
    private readonly FakeSlugCache _cache = new();
    private readonly FakeRedirectRepo _repo = new();
    private readonly FakeClickPublisher _publisher = new();

    private RedirectController NewController(Action<DefaultHttpContext>? configure = null)
    {
        var resolver = new CachedSlugResolver(_cache, _repo, _clock, TimeSpan.FromHours(12));
        var ctrl = new RedirectController(resolver, _publisher, _clock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        configure?.Invoke((DefaultHttpContext)ctrl.ControllerContext.HttpContext);
        return ctrl;
    }

    [Fact]
    public async Task RedirectTo_SlugFound_ReturnsRedirectToUrl()
    {
        var link = NewLink("abc123", url: "https://example.com/target");
        _repo.BySlug["abc123"] = link;
        var ctrl = NewController();

        var result = await ctrl.RedirectTo("abc123", default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://example.com/target", redirect.Url);
    }

    [Fact]
    public async Task RedirectTo_SlugFound_EmitsClickEventWithLinkId()
    {
        var link = NewLink("abc123");
        _repo.BySlug["abc123"] = link;
        var ctrl = NewController(ctx => ctx.Request.Headers.UserAgent = "Mozilla/5.0 (iPhone)");
        ctrl.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");

        await ctrl.RedirectTo("abc123", default);

        var evt = Assert.Single(_publisher.Published);
        Assert.Equal(link.Id, evt.ShortenedUrlId);
        Assert.Equal("1.2.3.4", evt.IpAddress);
        Assert.Equal(DeviceType.Mobile, evt.Device);
    }

    [Fact]
    public async Task RedirectTo_SlugNotFound_ReturnsNotFoundAndNoEvent()
    {
        var ctrl = NewController();

        var result = await ctrl.RedirectTo("nope", default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task RedirectTo_ExpiredSlug_ReturnsNotFoundAndNoEvent()
    {
        _repo.BySlug["old"] = NewLink("old", expiresAt: _clock.UtcNow.AddHours(-1));
        var ctrl = NewController();

        var result = await ctrl.RedirectTo("old", default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task RedirectTo_WhenPublisherThrows_StillReturnsRedirectToUrl()
    {
        var link = NewLink("abc123", url: "https://example.com/target");
        _repo.BySlug["abc123"] = link;
        _publisher.ExceptionToThrow = _ => new InvalidOperationException("Broker unreachable");
        var ctrl = NewController();

        var result = await ctrl.RedirectTo("abc123", default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://example.com/target", redirect.Url);
    }

    [Fact]
    public async Task RedirectTo_WhenPublisherThrowsInternalTimeout_StillReturnsRedirectToUrl()
    {
        var link = NewLink("abc123", url: "https://example.com/target");
        _repo.BySlug["abc123"] = link;
        _publisher.ExceptionToThrow = _ => new TaskCanceledException("Connect timeout");
        var ctrl = NewController();

        var result = await ctrl.RedirectTo("abc123", default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://example.com/target", redirect.Url);
    }

    [Fact]
    public async Task RedirectTo_WhenPublisherCancelledByRequest_PropagatesOperationCanceledException()
    {
        var link = NewLink("abc123", url: "https://example.com/target");
        _repo.BySlug["abc123"] = link;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _publisher.ExceptionToThrow = ct => new OperationCanceledException(ct);
        var ctrl = NewController();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ctrl.RedirectTo("abc123", cts.Token));
    }

    private static ShortenedUrl NewLink(string slug, string? url = null, DateTimeOffset? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Url = url ?? "https://example.com/" + slug,
        OwnerId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt
    };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeRedirectRepo : IShortLinkRepository
    {
        public Dictionary<string, ShortenedUrl> BySlug { get; } = new();

        public Task<ShortenedUrl?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.TryGetValue(slug, out var l) ? l : null);

        public Task<ShortenedUrl?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<ShortenedUrl?>(null);

        public Task<List<ShortenedUrl>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default)
            => Task.FromResult(new List<ShortenedUrl>());

        public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(BySlug.ContainsKey(slug));

        public Task AddAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(ShortenedUrl link, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeClickPublisher : IClickEventPublisher
    {
        public List<ClickEvent> Published { get; } = new();
        public Func<CancellationToken, Exception>? ExceptionToThrow { get; set; }

        public Task PublishAsync(ClickEvent clickEvent, CancellationToken ct = default)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow(ct);

            Published.Add(clickEvent);
            return Task.CompletedTask;
        }
    }
}
