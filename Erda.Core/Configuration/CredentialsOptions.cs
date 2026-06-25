using System.ComponentModel.DataAnnotations;

namespace Erda.Core.Configuration;

/// <summary>
/// The cloud credentials Erda needs, bound from flat (unprefixed) environment variables and
/// <b>validated at startup</b> — a missing or blank value stops the app from booting (see the
/// <c>ValidateOnStart</c> wiring in <c>AddErdaCore</c>) rather than failing later on the first call.
/// <para>
/// Only transcription needs a real platform key here; the chat model now runs against the local
/// OpenAI-compatible endpoint (its base URL/key live in <see cref="ErdaOptions"/>). The name is kept
/// identical to the env var via <see cref="ConfigurationKeyNameAttribute"/> so <c>.env</c> / compose
/// stay unchanged.
/// </para>
/// </summary>
public sealed class CredentialsOptions
{
    /// <summary>OpenAI-platform key used for speech-to-text.</summary>
    [Required(AllowEmptyStrings = false)]
    [ConfigurationKeyName("OPENAI_API_KEY")]
    public string OpenAIApiKey { get; set; } = "";
}
