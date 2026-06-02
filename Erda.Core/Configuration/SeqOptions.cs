namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the (remote) Seq log server (bound from the "Seq" config section). Used two ways:
/// the error-watch scheduler QUERIES it for new errors, and — when <see cref="IngestToErda"/> is
/// true — Erda also SHIPS its own logs there via Serilog so its errors show up alongside the rest.
/// </summary>
public sealed class SeqOptions
{
    public const string SectionName = "Seq";

    /// <summary>Base URL of the Seq server, e.g. "https://seq.example.com".</summary>
    public string? ServerUrl { get; set; }

    /// <summary>API key with read/query permission (also used for ingestion if <see cref="IngestToErda"/>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Ship Erda's own Serilog output to Seq (so Erda's errors are queryable too).</summary>
    public bool IngestToErda { get; set; } = true;

    public bool HasServer => !string.IsNullOrWhiteSpace(ServerUrl);
}
