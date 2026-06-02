namespace Erda.Core.Configuration;

/// <summary>
/// Settings bound from the "Observability" section. Controls the OpenTelemetry tracing that
/// MAF emits for agent runs, model calls, and tool/function invocations.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// OpenTelemetry ActivitySource name the agent emits spans on (passed to the agent's
    /// UseOpenTelemetry) and that the tracer subscribes to (AddSource). Keep both in sync.
    /// </summary>
    public const string ActivitySourceName = "Erda.Agent";

    /// <summary>Master switch for OpenTelemetry tracing. When false, no spans are emitted/exported.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, prompt / completion / tool-argument <b>content</b> is captured in spans (and in
    /// the Codex launch log). When false (the default, and the production posture) only metadata is
    /// recorded — tool names, durations, token counts, success/failure — never message text.
    /// Translated at startup into the standard env var
    /// <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>.
    /// </summary>
    public bool CaptureMessageContent { get; set; } = false;
}
