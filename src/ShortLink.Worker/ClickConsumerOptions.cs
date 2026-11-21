namespace ShortLink.Worker;

/// <summary>Konfigurasi resiliensi consumer RabbitMQ: retry/backoff, reconnect, prefetch, dan dead-letter queue.</summary>
public sealed class ClickConsumerOptions
{
    public const string SectionName = "RabbitMQ:Consumer";

    public int MaxRetryAttempts { get; set; } = 3;
    public int InitialRetryDelayMs { get; set; } = 500;
    public int MaxRetryDelayMs { get; set; } = 10_000;
    public int InitialConnectDelayMs { get; set; } = 1_000;
    public int MaxConnectDelayMs { get; set; } = 30_000;
    public ushort PrefetchCount { get; set; } = 10;
    public string DeadLetterExchange { get; set; } = "clicks.dlx";
    public string DeadLetterQueue { get; set; } = "clicks.dlq";
    public string DeadLetterRoutingKey { get; set; } = "click.dead";

    public TimeSpan InitialRetryDelay => TimeSpan.FromMilliseconds(Math.Max(0, InitialRetryDelayMs));
    public TimeSpan MaxRetryDelay => TimeSpan.FromMilliseconds(Math.Max(0, MaxRetryDelayMs));
    public TimeSpan InitialConnectDelay => TimeSpan.FromMilliseconds(Math.Max(0, InitialConnectDelayMs));
    public TimeSpan MaxConnectDelay => TimeSpan.FromMilliseconds(Math.Max(0, MaxConnectDelayMs));

    public static TimeSpan ComputeBackoffDelay(int attempt, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var effectiveMax = maxDelay > TimeSpan.Zero ? maxDelay : TimeSpan.MaxValue;
        var effectiveInitial = initialDelay > effectiveMax ? effectiveMax : initialDelay;
        if (attempt <= 1)
            return effectiveInitial;

        var exponent = Math.Min(attempt - 1, 30);
        var computedMs = effectiveInitial.TotalMilliseconds * Math.Pow(2, exponent);
        if (computedMs >= effectiveMax.TotalMilliseconds)
            return effectiveMax;

        return TimeSpan.FromMilliseconds(computedMs);
    }

    internal static Task DelayAsync(TimeSpan delay, CancellationToken ct)
        => delay > TimeSpan.Zero ? Task.Delay(delay, ct) : Task.CompletedTask;
}
