using System.ComponentModel.DataAnnotations;

namespace Erda.Core.Configuration;

/// <summary>
/// Strongly-typed settings bound from the "Erda" section of configuration. The credentials live in
/// <see cref="CredentialsOptions"/>, NOT here.
/// <para>
/// Every value is required and carries no default — it must be set in <c>.env</c>, and a missing one
/// stops the app at startup (validated via DataAnnotations). The values shown in <c>.env.example</c>
/// are the conventional ones (model names, the local endpoint URL), not in-code fallbacks.
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

    /// <summary>OpenAI-compatible base URL for the chat/reasoning model (e.g. the local proxy's
    /// <c>http://127.0.0.1:10531/v1</c>). The OpenAI SDK is pointed here for both the Erda agent and the
    /// in-process reasoner; any OpenAI-compatible provider's base URL works.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatBaseUrl { get; set; } = "";

    /// <summary>Model id for the chat/reasoning calls against <see cref="ChatBaseUrl"/> (e.g. <c>gpt-5.5</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatModel { get; set; } = "";

    /// <summary>
    /// The reasoning-effort levels accepted for <see cref="ChatReasoningEffort"/>. The single source of
    /// truth: startup validation checks the configured value against this set, and the reasoner
    /// normalizes against it. <c>minimal</c> is intentionally excluded — Erda always attaches the hosted
    /// <c>web_search</c> tool, and the Responses API rejects minimal effort combined with hosted tools.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidReasoningEfforts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high" };

    /// <summary>Reasoning effort for the chat/reasoning model (low/medium/high). Required, no default —
    /// it must be set in <c>.env</c> and is validated against <see cref="ValidReasoningEfforts"/> at
    /// startup. The same value drives both the orchestrator and the in-process reasoner.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatReasoningEffort { get; set; } = "";

    /// <summary>Optional API key for <see cref="ChatBaseUrl"/>. The loopback proxy needs no real auth,
    /// but the OpenAI SDK still requires a non-empty credential string — blank falls back to the dummy
    /// <c>"local"</c>. Not required.</summary>
    public string ChatApiKey { get; set; } = "local";

    /// <summary>OpenAI-platform model used for speech-to-text (e.g. <c>gpt-4o-transcribe</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string TranscribeModel { get; set; } = "";

    /// <summary>Vault-relative subfolder where processed voice memos are saved.</summary>
    [Required(AllowEmptyStrings = false)]
    public string VoiceMemoSubfolder { get; set; } = "";
}
