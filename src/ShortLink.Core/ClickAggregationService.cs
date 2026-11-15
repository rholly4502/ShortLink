using ShortLink.Core.Abstractions;
using ShortLink.Core.Entities;

namespace ShortLink.Core;

/// <summary>
/// Menghitung agregasi klik per periode (hourly & daily) per device/country
/// dan mempersist-nya ke click_aggregates (P4.2).
/// Reuse logika bucket murni dari ShortLinkService.AggregateClicks.
/// </summary>
public sealed class ClickAggregationService
{
    private readonly IClickRepository _clicks;

    public ClickAggregationService(IClickRepository clicks) => _clicks = clicks;

    public async Task AggregateAsync(IEnumerable<Click> clicks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clicks);

        var hourly = ShortLinkService.AggregateClicks(clicks, AggregatePeriod.Hourly);
        var daily = ShortLinkService.AggregateClicks(clicks, AggregatePeriod.Daily);

        await _clicks.UpsertAggregatesAsync(hourly.Concat(daily), ct);
    }
}
