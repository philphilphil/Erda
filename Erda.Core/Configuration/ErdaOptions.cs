using System.ComponentModel.DataAnnotations;

namespace Erda.Core.Configuration;

/// <summary>
/// Strongly-typed settings bound from the "Erda" section of configuration. The credentials live in
/// <see cref="CredentialsOptions"/>, NOT here.
/// <para>
/// Two kinds of property live here. <b>Required deployment settings</b> (<see cref="VaultPath"/>,
/// <see cref="DbPath"/>) carry no default and are validated at startup — a missing value stops the
/// app. Everything else holds an <b>invariant value</b>: it is intentionally absent from every
/// config file, so the value below is the single source. Change those in code, not via config.
/// </para>
/// </summary>
public sealed class ErdaOptions
{
    public const string SectionName = "Erda";

    /// <summary>Absolute path to the Obsidian vault root that Erda may read/write. Required.</summary>
    [Required(AllowEmptyStrings = false)]
    public string VaultPath { get; set; } = "";

    /// <summary>
    /// SQLite database file for all runtime state (prompt versions, reminders, error-watch state,
    /// activity). Required — set to a bind-mounted path in the container (e.g.
    /// <c>/data/erda/erda.db</c>) so it survives redeploys, or a local path in dev.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DbPath { get; set; } = "";

    /// <summary>Azure AI Foundry deployment name for the chat model (gpt-5-mini).</summary>
    public string ChatDeployment { get; set; } = "gpt-5-mini";

    /// <summary>OpenAI-platform model used for speech-to-text.</summary>
    public string TranscribeModel { get; set; } = "gpt-4o-transcribe";

    /// <summary>
    /// Model passed to <c>codex exec -m</c> (runs on the ChatGPT subscription).
    /// NOTE: must be a model the ChatGPT subscription supports. <c>gpt-5-codex</c> is API-only
    /// ("not supported when using Codex with a ChatGPT account"); <c>gpt-5.5</c> is the newest
    /// model available on the subscription (matches ~/.codex/config.toml).
    /// </summary>
    public string CodexModel { get; set; } = "gpt-5.5";

    /// <summary>Reasoning effort passed to <c>codex exec -c model_reasoning_effort</c>.</summary>
    public string CodexReasoningEffort { get; set; } = "high";

    /// <summary>Max wall-clock time for a single <c>codex exec</c> before it is killed (guards against hangs).</summary>
    public TimeSpan CodexTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The codex CLI executable (path or name on PATH). Overridable mainly for tests.</summary>
    public string CodexExecutable { get; set; } = "codex";

    /// <summary>Vault-relative subfolder where processed voice memos are saved.</summary>
    public string VoiceMemoSubfolder { get; set; } = "VoiceMemos";
}
