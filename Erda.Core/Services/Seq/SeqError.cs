namespace Erda.Core.Services.Seq;

/// <summary>
/// A normalized error event read from Seq — decoupled from the Seq.Api <c>EventEntity</c> so the
/// scheduler, signature, and alert logic are pure and unit-testable without the Seq client.
/// </summary>
public sealed record SeqError
{
    public string Id { get; init; } = "";

    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Seq level string, e.g. "Error" or "Fatal".</summary>
    public string Level { get; init; } = "";

    /// <summary>The message template (with {Property} placeholders) — stable across occurrences.</summary>
    public string MessageTemplate { get; init; } = "";

    /// <summary>The fully rendered message (placeholders filled) — for display.</summary>
    public string RenderedMessage { get; init; } = "";

    /// <summary>Exception type name parsed from the first line of the exception, if any.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Full exception text, if any.</summary>
    public string? Exception { get; init; }

    /// <summary>A few notable properties (e.g. Application, SourceContext) for context.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Best display text: rendered message, falling back to the template.</summary>
    public string Display => string.IsNullOrWhiteSpace(RenderedMessage) ? MessageTemplate : RenderedMessage;
}
