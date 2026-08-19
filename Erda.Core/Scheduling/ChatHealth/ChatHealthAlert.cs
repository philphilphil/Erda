using System.Text;

namespace Erda.Core.Scheduling;

/// <summary>
/// Formats the two chat-health messages Phil gets on WhatsApp: the endpoint stopped answering, and
/// it answers again. Both name the endpoint and model, because the fix (restart the proxy, log it
/// back in) happens on the box hosting them.
/// </summary>
public static class ChatHealthAlert
{
    private const int MaxLength = 3500; // WhatsApp hard limit is ~4096; leave headroom.

    /// <summary>The "it's down" alert. <paramref name="downFor"/> is null on the first alert of an outage.</summary>
    public static string FormatDown(string baseUrl, string model, string? error, TimeSpan? downFor = null)
    {
        var sb = new StringBuilder();
        sb.Append(downFor is null
            ? "🔌 OpenAI proxy is not answering."
            : $"🔌 OpenAI proxy is still down ({Humanize(downFor.Value)}).");
        sb.Append("\nEndpoint: ").Append(Show(baseUrl));
        sb.Append("\nModel: ").Append(Show(model));
        if (!string.IsNullOrWhiteSpace(error))
            sb.Append("\nReason: ").Append(error!.Trim());
        sb.Append("\n\nThe chat agent, voice memos and scheduled prompts stay broken until it's back — check whether the proxy is running and still logged in.");
        return Truncate(sb.ToString(), MaxLength);
    }

    /// <summary>The recovery notice, sent once the endpoint answers again after an alerted outage.</summary>
    public static string FormatRecovered(string baseUrl, TimeSpan downFor) =>
        $"✅ OpenAI proxy is answering again (down for {Humanize(downFor)}).\nEndpoint: {Show(baseUrl)}";

    /// <summary>Coarse, WhatsApp-readable duration: "3 minutes", "2 hours", "1 day".</summary>
    public static string Humanize(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1)
            return "less than a minute";
        if (span.TotalHours < 1)
            return Plural((int)span.TotalMinutes, "minute");
        if (span.TotalDays < 1)
            return Plural((int)span.TotalHours, "hour");
        return Plural((int)span.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    private static string Show(string? v) => string.IsNullOrWhiteSpace(v) ? "(not set)" : v.Trim();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
