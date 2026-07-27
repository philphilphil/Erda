namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the OpenAI/chat connection health check (bound from the "HealthCheck" config
/// section). On an interval it sends a tiny probe prompt through <see cref="Services.IReasoner"/> —
/// the same streamed Responses path the chat agent and voice-memo/error-watch reasoning use — to
/// confirm the local OpenAI-compatible endpoint (the codex/proxy) is still answering. When the probe
/// fails (error, timeout, or empty output) it alerts Phil over WhatsApp, and sends a follow-up when
/// the connection recovers. The settings below are required (no default) only when
/// <see cref="Enabled"/> is true — see <c>HealthCheckOptionsValidator</c>.
/// </summary>
public sealed class HealthCheckOptions
{
    public const string SectionName = "HealthCheck";

    /// <summary>Default probe timeout when <see cref="Timeout"/> is not set.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Master switch for the background health check. Absent ⇒ off.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to probe the chat endpoint (e.g. hourly). Required when enabled.</summary>
    public TimeSpan Interval { get; set; }

    /// <summary>
    /// How long a single probe may run before it counts as a failure. Optional; absent (or
    /// non-positive) ⇒ <see cref="DefaultTimeout"/>. Guards against a hung connection blocking the loop.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// While the connection stays down, how long to wait after alerting before re-alerting on the
    /// ongoing outage. Absent ⇒ alert once when it goes down (and once again when it recovers), never
    /// repeating in between.
    /// </summary>
    public TimeSpan? ReAlertAfter { get; set; }

    /// <summary><see cref="Timeout"/> folded to a positive value, falling back to <see cref="DefaultTimeout"/>.</summary>
    public TimeSpan EffectiveTimeout =>
        Timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
}
