namespace Erda.Configuration;

/// <summary>
/// Strongly-typed settings bound from the "Erda" section of configuration.
/// Secrets (the three API credentials) live in environment variables, NOT here.
/// </summary>
public sealed class ErdaOptions
{
    public const string SectionName = "Erda";

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

    /// <summary>Absolute path to the Obsidian vault root that Erda may read/write.</summary>
    public string VaultPath { get; set; } = "/Users/phil/TestingNotes";

    /// <summary>Vault-relative subfolder where processed voice memos are saved.</summary>
    public string VoiceMemoSubfolder { get; set; } = "VoiceMemos";
}
