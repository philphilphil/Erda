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

    /// <summary>OpenAI-compatible base URL of the LiteLLM endpoint that fronts the chat model (e.g.
    /// <c>http://litellm.lan:4000/v1</c>). Every chat/reasoning call goes here: the Erda agent, the
    /// in-process reasoner and the vault editor.</summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string ChatBaseUrl { get; set; } = "";

    /// <summary>API key for <see cref="ChatBaseUrl"/> (the LiteLLM virtual key).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatApiKey { get; set; } = "";

    /// <summary>Model id served by <see cref="ChatBaseUrl"/> for every chat/reasoning call (e.g. <c>claude-opus-5-5</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatModel { get; set; } = "";

    /// <summary>
    /// The reasoning-effort levels accepted for <see cref="ChatReasoningEffort"/>. The single source of
    /// truth: startup validation checks the configured value against it. These three are the levels
    /// every provider behind LiteLLM understands.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidReasoningEfforts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high" };

    /// <summary>Reasoning effort for the chat/reasoning model (low/medium/high). Required, no default —
    /// it must be set in <c>.env</c> and is validated against <see cref="ValidReasoningEfforts"/> at
    /// startup. The same value drives the orchestrator and the in-process reasoner; the vault editor
    /// always runs at high.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatReasoningEffort { get; set; } = "";

    /// <summary>How many recent non-system messages a long-lived chat session (WhatsApp, web chat)
    /// replays to the model each turn. Older messages are dropped, which caps per-turn cost.
    /// Required, no default.</summary>
    [Required]
    [Range(2, 10_000)]
    public int? ChatHistoryMaxMessages { get; set; }

    /// <summary>OpenAI-platform model used for speech-to-text (e.g. <c>gpt-4o-transcribe</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string TranscribeModel { get; set; } = "";

    /// <summary>Vault-relative subfolder where processed voice memos are saved.</summary>
    [Required(AllowEmptyStrings = false)]
    public string VoiceMemoSubfolder { get; set; } = "";
}
