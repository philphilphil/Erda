using System.ComponentModel.DataAnnotations;

namespace Erda.Core.Configuration;

/// <summary>
/// Strongly-typed settings bound from the "Erda" section of configuration. The credentials live in
/// <see cref="CredentialsOptions"/>, NOT here.
/// <para>
/// Every value is required and carries no default — it must be set in <c>.env</c>, and a missing one
/// stops the app at startup (validated via DataAnnotations). The values shown in <c>.env.example</c>
/// are the conventional ones (model names, the codex binary), not in-code fallbacks.
/// </para>
/// </summary>
public sealed class ErdaOptions
{
    public const string SectionName = "Erda";

    /// <summary>Absolute path to the Obsidian vault root that Erda may read/write.</summary>
    [Required(AllowEmptyStrings = false)]
    public string VaultPath { get; set; } = "";

    /// <summary>
    /// SQLite database file for all runtime state (prompt versions, reminders, error-watch state,
    /// activity). Set to a bind-mounted path in the container (e.g. <c>/data/erda/erda.db</c>) so it
    /// survives redeploys, or a local path in dev.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DbPath { get; set; } = "";

    /// <summary>Azure AI Foundry deployment name for the chat model (e.g. <c>gpt-5-mini</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatDeployment { get; set; } = "";

    /// <summary>OpenAI-platform model used for speech-to-text (e.g. <c>gpt-4o-transcribe</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string TranscribeModel { get; set; } = "";

    /// <summary>
    /// Model passed to <c>codex exec -m</c> (runs on the ChatGPT subscription).
    /// NOTE: must be a model the ChatGPT subscription supports. <c>gpt-5-codex</c> is API-only
    /// ("not supported when using Codex with a ChatGPT account"); <c>gpt-5.5</c> is the newest
    /// model available on the subscription (matches ~/.codex/config.toml).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string CodexModel { get; set; } = "";

    /// <summary>Reasoning effort passed to <c>codex exec -c model_reasoning_effort</c> (low/medium/high).</summary>
    [Required(AllowEmptyStrings = false)]
    public string CodexReasoningEffort { get; set; } = "";

    /// <summary>Max wall-clock time for a single <c>codex exec</c> before it is killed (guards against hangs).</summary>
    [PositiveTimeSpan]
    public TimeSpan CodexTimeout { get; set; }

    /// <summary>The codex CLI executable (path or name on PATH; normally <c>codex</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string CodexExecutable { get; set; } = "";

    /// <summary>Vault-relative subfolder where processed voice memos are saved.</summary>
    [Required(AllowEmptyStrings = false)]
    public string VoiceMemoSubfolder { get; set; } = "";
}
