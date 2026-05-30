using Erda.Configuration;
using Erda.Services;
using Erda.Workflows.Executors;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;

namespace Erda.Workflows;

/// <summary>
/// The "voice-memo" MAF workflow: a chat-protocol input adapter plus a three-step chain.
///
///   Input (chat -> path) -> Transcribe (.m4a -> text) -> Codex (text -> note) -> ObsidianWrite (note -> ChatMessage)
///
/// The three middle steps are plain string executors (Codex is shelled out, not an AIAgent
/// node). But when a workflow is hosted as an AIAgent (DevUI / OpenAI-Responses), its START
/// executor must speak the chat protocol (accept List&lt;ChatMessage&gt; + TurnToken) and its
/// OUTPUT must be a ChatMessage to surface in the response. So the chain is bookended by
/// <see cref="VoiceMemoInputExecutor"/> (chat -> path string) at the front and a
/// ChatMessage-returning <see cref="ObsidianWriteExecutor"/> at the end.
/// </summary>
public static class VoiceMemoWorkflow
{
    public const string Name = "voice-memo";

    /// <summary>Developer instruction prepended to the transcript before handing it to Codex.</summary>
    public const string DeveloperInstruction =
        "You are processing a transcribed Apple Voice Memo for Phil's Obsidian vault. " +
        "Clean up the transcript (fix obvious speech-to-text errors and remove filler words), " +
        "then extract any todos, notes, and ideas. Produce ONE well-structured Markdown note with:\n" +
        "- a short '# Title' heading,\n" +
        "- a one-line summary,\n" +
        "- an '## Action items' section using '- [ ] ' task checkboxes where appropriate,\n" +
        "- a '## Notes' section for everything else,\n" +
        "- a final '## Transcript' section containing the cleaned transcript.\n" +
        "Output only the Markdown note, with no preamble or commentary.";

    public static Workflow Build(IServiceProvider services)
    {
        var input = new VoiceMemoInputExecutor();
        var transcribe = new TranscribeExecutor(services.GetRequiredService<Transcriber>());
        var codex = new CodexExecutor(services.GetRequiredService<CodexRunner>());
        var write = new ObsidianWriteExecutor(
            services.GetRequiredService<VaultService>(),
            services.GetRequiredService<IOptions<ErdaOptions>>().Value);

        return new WorkflowBuilder(input)
            .AddEdge(input, transcribe)
            .AddEdge(transcribe, codex)
            .AddEdge(codex, write)
            .WithOutputFrom(write)
            .WithName(Name) // must equal the registration key, or DevUI enumeration throws
            .Build();
    }

    /// <summary>
    /// Wrap the workflow as an AIAgent for DevUI. <c>includeWorkflowOutputsInResponse: true</c>
    /// is required for the terminal ChatMessage to appear as the agent's response.
    /// </summary>
    public static AIAgent CreateAgent(IServiceProvider services)
        => Build(services).AsAIAgent(
            id: Name,
            name: Name,
            description: "Transcribe an Apple Voice Memo (.m4a path), process it with Codex, and save a note to the vault.",
            includeWorkflowOutputsInResponse: true);
}

/// <summary>Shared helper so the workflow and the conversational tool write memos identically.</summary>
public static class VoiceMemoWriter
{
    public static string Write(VaultService vault, ErdaOptions options, string content)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmmss");
        var subfolder = options.VoiceMemoSubfolder.Trim().Trim('/');
        var relative = string.IsNullOrEmpty(subfolder) ? $"{stamp}.md" : $"{subfolder}/{stamp}.md";
        vault.WriteNote(relative, content);
        return $"Saved voice memo to {relative} ({content.Length} chars).";
    }
}
