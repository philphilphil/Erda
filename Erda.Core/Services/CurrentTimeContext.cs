using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>
/// Produces a small system message stating the current local time, prepended to agent turns so the
/// model can resolve relative phrases ("tomorrow at 9") into concrete schedules. Uses the same
/// timezone as the reminder scheduler.
/// </summary>
public sealed class CurrentTimeContext(IClock clock, IOptions<ReminderOptions> options)
{
    /// <summary>The current-time context as a system <see cref="ChatMessage"/> for an agent turn.</summary>
    public ChatMessage Message() => new(ChatRole.System, Text());

    /// <summary>
    /// The current-time context line as plain text (what <see cref="Message"/> wraps). Used by the
    /// Codex-direct scheduled-prompt path, which prepends it so Codex knows the date.
    /// </summary>
    public string Text()
    {
        var zone = ResolveZone();
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        return $"[Context] Current time: {local:yyyy-MM-dd HH:mm} ({zone.Id}, {local:dddd}).";
    }

    private TimeZoneInfo ResolveZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone); }
        catch { return TimeZoneInfo.Utc; }
    }
}
