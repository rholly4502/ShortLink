using ShortLink.Core.Abstractions;

namespace ShortLink.Core;

/// <summary>IClock sungguhan memakai DateTimeOffset.UtcNow.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
