namespace ShortLink.Core.Abstractions;

/// <summary>Abstraksi waktu agar logika domain mudah diuji.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
