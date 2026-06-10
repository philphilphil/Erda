namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the error-watch scheduler (bound from the "ErrorWatch" config section). On an
/// interval it polls Seq for new errors, asks Codex to analyze each new one, and pushes the
/// analysis to Phil over WhatsApp. The settings below are required (no default) only when
/// <see cref="Enabled"/> is true — see <c>ErrorWatchOptionsValidator</c>.
/// </summary>
public sealed class ErrorWatchOptions
{
    public const string SectionName = "ErrorWatch";

    /// <summary>Master switch for the background scheduler. Absent ⇒ off.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to poll Seq. Required when enabled.</summary>
    public TimeSpan PollInterval { get; set; }

    /// <summary>Minimum Seq level to alert on (e.g. "Error" also catches "Fatal"). Required when enabled.</summary>
    public string MinLevel { get; set; } = "";

    /// <summary>Optional Seq filter expression to scope the query (e.g. by Application property).</summary>
    public string? Filter { get; set; }

    /// <summary>Where the watermark + seen-signatures state is persisted. Null = a default app-data path.</summary>
    public string? StateFile { get; set; }

    /// <summary>Safety cap: at most this many alerts per poll (prevents a storm on first/large batches). Required when enabled.</summary>
    public int MaxAlertsPerPoll { get; set; }

    /// <summary>Run each new error through Codex for analysis; when off, send the raw error. Absent ⇒ off.</summary>
    public bool AnalyzeWithCodex { get; set; }
}
