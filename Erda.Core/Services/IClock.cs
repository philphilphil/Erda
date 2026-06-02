namespace Erda.Core.Services;

/// <summary>Abstracts "now" so time-dependent code (scheduler, current-time context) is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
