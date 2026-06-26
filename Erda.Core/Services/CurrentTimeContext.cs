using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>
/// Produces a small context message stating the current local time, prepended to agent turns so the
/// model can resolve relative phrases ("tomorrow at 9") into concrete schedules. Uses the same
/// timezone as the reminder scheduler.
/// </summary>
public sealed class CurrentTimeContext(IClock clock, IOptions<ReminderOptions> options)
{
    /// <summary>
    /// The current-time context as a <see cref="ChatMessage"/> for an agent turn. Uses the
    /// <see cref="ChatRole.User"/> role on purpose: the chat endpoint (codex-oauth proxy) rejects
    /// <c>system</c>-role items in a Responses request's input ("System messages are not allowed") —
    /// the agent's system prompt rides the top-level <c>instructions</c> field instead. The "[Context]"
    /// prefix keeps it distinct from the user's own text.
    /// </summary>
    public ChatMessage Message() => new(ChatRole.User, Text());

    /// <summary>The current-time context line as plain text (what <see cref="Message"/> wraps).</summary>
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
