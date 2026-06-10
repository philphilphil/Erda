using System.ComponentModel.DataAnnotations;

namespace Erda.Core.Configuration;

/// <summary>
/// The cloud credentials Erda needs, bound from flat (unprefixed) environment variables and
/// <b>validated at startup</b> — a missing or blank value stops the app from booting (see the
/// <c>ValidateOnStart</c> wiring in <c>AddErdaCore</c>) rather than failing later on the first call.
/// <para>
/// These are the two of the three-credential model that are plain keys; Codex authenticates via the
/// mounted ChatGPT session, not a key here. Names are kept identical to the env vars via
/// <see cref="ConfigurationKeyNameAttribute"/> so <c>.env</c> / compose stay unchanged.
/// </para>
/// </summary>
public sealed class CredentialsOptions
{
    /// <summary>Azure AI Foundry endpoint for the chat model (e.g. <c>https://…services.ai.azure.com/</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    [ConfigurationKeyName("AZURE_OPENAI_ENDPOINT")]
    public string AzureOpenAIEndpoint { get; set; } = "";

    /// <summary>Azure AI Foundry API key for the chat model.</summary>
    [Required(AllowEmptyStrings = false)]
    [ConfigurationKeyName("AZURE_OPENAI_API_KEY")]
    public string AzureOpenAIApiKey { get; set; } = "";

    /// <summary>OpenAI-platform key used for speech-to-text (stripped from the Codex subprocess).</summary>
    [Required(AllowEmptyStrings = false)]
    [ConfigurationKeyName("OPENAI_API_KEY")]
    public string OpenAIApiKey { get; set; } = "";
}
