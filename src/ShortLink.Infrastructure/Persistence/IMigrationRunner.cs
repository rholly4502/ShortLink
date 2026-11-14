namespace ShortLink.Infrastructure.Persistence;

public interface IMigrationRunner
{
    Task RunAsync(CancellationToken ct = default);
}
