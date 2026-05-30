using System.ClientModel;
using Azure.AI.OpenAI;
using Erda.Configuration;
using Erda.Services;
using Erda.Tools;
using Erda.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Erda.Agents;

/// <summary>
/// Builds the Erda chat agent: gpt-5-mini on Azure AI Foundry, reached via the Azure OpenAI
/// client with API-key auth. Tools = the five Obsidian vault tools plus a process_voice_memo
/// convenience tool that runs the same Transcribe -> Codex -> Obsidian pipeline.
/// </summary>
public static class ErdaAgent
{
    public const string Name = "erda";

    private const string Instructions = """
        You are Erda, Phil's concise personal assistant.
        You can browse and edit his Obsidian vault with these tools:
        list_notes, read_note, search_notes, write_note, append_note.
        You can also process an Apple Voice Memo end-to-end with process_voice_memo (give it an absolute .m4a path).
        Prefer reading or searching before writing, and confirm before overwriting an existing note.
        Keep answers short and practical.
        """;

    public static AIAgent Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = services.GetRequiredService<IOptions<ErdaOptions>>().Value;

        // Foundry endpoint + key. If unset we still construct the agent (so the app and DevUI
        // start) using a placeholder; the actual call fails clearly until the env vars are set.
        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        var configured = !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey);

        ChatClient chatClient = new AzureOpenAIClient(
                new Uri(configured ? endpoint! : "https://erda-unconfigured.invalid"),
                new ApiKeyCredential(configured ? apiKey! : "unconfigured"))
            .GetChatClient(options.ChatDeployment);

        var obsidian = services.GetRequiredService<ObsidianTools>();
        var tools = new List<AITool>(obsidian.AsTools())
        {
            AIFunctionFactory.Create(
                BuildProcessVoiceMemo(services),
                "process_voice_memo",
                "Transcribe an Apple Voice Memo (.m4a), process the transcript with Codex, and save the result as a note in the vault. Returns a confirmation."),
        };

        // The agent's name MUST equal the registration key (see Program.cs AddAIAgent).
        return chatClient.AsAIAgent(instructions: Instructions, name: Name, tools: tools);
    }

    // Stretch goal: the same pipeline as the voice-memo workflow, callable conversationally.
    private static Func<string, Task<string>> BuildProcessVoiceMemo(IServiceProvider services)
    {
        var transcriber = services.GetRequiredService<Transcriber>();
        var codex = services.GetRequiredService<CodexRunner>();
        var vault = services.GetRequiredService<VaultService>();
        var options = services.GetRequiredService<IOptions<ErdaOptions>>().Value;

        return async (string path) =>
        {
            var transcript = await transcriber.TranscribeAsync(path.Trim());
            var note = await codex.RunAsync(VoiceMemoWorkflow.DeveloperInstruction, transcript);
            return VoiceMemoWriter.Write(vault, options, note);
        };
    }
}
