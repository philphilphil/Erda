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
    public ChatMessage Message()
    {
        var zone = ResolveZone();
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        return new ChatMessage(ChatRole.System,
            $"[Context] Current time: {local:yyyy-MM-dd HH:mm} ({zone.Id}, {local:dddd}).");
    }

    private TimeZoneInfo ResolveZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone); }
        catch { return TimeZoneInfo.Utc; }
    }
}
