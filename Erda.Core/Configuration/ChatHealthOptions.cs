namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the chat-endpoint health watch (bound from the "ChatHealth" config section). On an
/// interval it sends a tiny prompt through the same path the agent uses — the local
/// OpenAI-compatible proxy at <see cref="ErdaOptions.ChatBaseUrl"/> — and pings Phil over WhatsApp
/// when the endpoint stops answering (proxy down, logged out, model unavailable). The settings below
/// are required (no default) only when <see cref="Enabled"/> is true — see
/// <c>ChatHealthOptionsValidator</c>.
/// </summary>
public sealed class ChatHealthOptions
{
    public const string SectionName = "ChatHealth";

    /// <summary>Master switch for the background watch. Absent ⇒ off.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to probe the chat endpoint. Required when enabled.</summary>
    public TimeSpan CheckInterval { get; set; }

    /// <summary>How long a single probe may take before it counts as a failure. Required when enabled.</summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>
    /// How long after alerting to stay quiet before a still-broken endpoint alerts again.
    /// Absent ⇒ one alert per outage (the recovery notice still fires when it comes back).
    /// </summary>
    public TimeSpan? ReAlertAfter { get; set; }
}
