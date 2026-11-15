namespace ShortLink.Core;

/// <summary>Ringkasan analytics klik untuk satu short link (turunan dari agregasi harian).</summary>
public sealed record AnalyticsSummary(
    Guid LinkId,
    long TotalClicks,
    IReadOnlyList<DeviceStat> ByDevice,
    IReadOnlyList<CountryStat> ByCountry,
    IReadOnlyList<DayStat> ByDay);

public sealed record DeviceStat(string Device, long Count);
public sealed record CountryStat(string Country, long Count);
public sealed record DayStat(DateTimeOffset Day, long Count);
