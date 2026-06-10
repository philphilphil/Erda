using System.ClientModel;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Audio;

namespace Erda.Core.Services;

/// <summary>Speech-to-text abstraction (so callers can be unit-tested without the OpenAI client).</summary>
public interface ITranscriber
{
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Speech-to-text via the OpenAI platform API. Uses the OPENAI_API_KEY (pay-per-token),
/// which is a DIFFERENT credential from the Azure/Foundry key used by the chat agent.
/// </summary>
public sealed class Transcriber(
    IOptions<ErdaOptions> options,
    IOptions<CredentialsOptions> credentials,
    ILogger<Transcriber> logger) : ITranscriber
{
    private const long MaxBytes = 25L * 1024 * 1024; // OpenAI transcription limit

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

        var info = new FileInfo(audioFilePath);
        if (info.Length > MaxBytes)
            throw new InvalidOperationException(
                $"Audio file is {info.Length / (1024 * 1024)} MB; the transcription API limit is 25 MB.");

        var apiKey = credentials.Value.OpenAIApiKey; // validated at startup; guaranteed present
        var model = options.Value.TranscribeModel;
        logger.LogInformation("Transcribing {File} with OpenAI model {Model} (platform key).", audioFilePath, model);

        AudioClient audio = new OpenAIClient(new ApiKeyCredential(apiKey)).GetAudioClient(model);
        AudioTranscription transcription = await audio.TranscribeAudioAsync(audioFilePath);
        return transcription.Text;
    }
}
