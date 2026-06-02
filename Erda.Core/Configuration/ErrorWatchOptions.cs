namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the error-watch scheduler (bound from the "ErrorWatch" config section). On an
/// interval it polls Seq for new errors, asks Codex to analyze each new one, and pushes the
/// analysis to Phil over WhatsApp.
/// </summary>
public sealed class ErrorWatchOptions
{
    public const string SectionName = "ErrorWatch";

    /// <summary>Master switch for the background scheduler.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to poll Seq.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum Seq level to alert on (e.g. "Error" also catches "Fatal").</summary>
    public string MinLevel { get; set; } = "Error";

    /// <summary>Optional Seq filter expression to scope the query (e.g. by Application property).</summary>
    public string? Filter { get; set; }

    /// <summary>Where the watermark + seen-signatures state is persisted. Null = a default app-data path.</summary>
    public string? StateFile { get; set; }

    /// <summary>Safety cap: at most this many alerts per poll (prevents a storm on first/large batches).</summary>
    public int MaxAlertsPerPoll { get; set; } = 5;

    /// <summary>When true, run each new error through Codex for analysis; when false, send the raw error.</summary>
    public bool AnalyzeWithCodex { get; set; } = true;
}
