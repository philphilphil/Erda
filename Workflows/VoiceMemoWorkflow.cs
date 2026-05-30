using Erda.Configuration;
using Erda.Services;
using Erda.Workflows.Executors;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
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

    /// <summary>Description shown to the orchestrator for the process_voice_memo tool.</summary>
    public const string ToolDescription =
        "Transcribe an Apple Voice Memo (.m4a), process the transcript with Codex, and save the " +
        "result as a structured note in the vault. Pass the absolute path to the .m4a file. " +
        "Returns a confirmation of where the note was saved.";

    /// <summary>
    /// Expose the workflow as an <see cref="AIFunction"/> tool the Erda orchestrator can call
    /// (agent-as-tool pattern). The workflow is first wrapped as an AIAgent — its ChatProtocol
    /// input adapter turns the tool's <c>query</c> argument (the .m4a path) into the workflow's
    /// starting message — then surfaced as a named function. <c>includeWorkflowOutputsInResponse</c>
    /// makes the terminal ChatMessage (the save confirmation) the tool's return value.
    /// </summary>
    public static AIFunction CreateTool(IServiceProvider services)
    {
        AIAgent workflowAgent = Build(services).AsAIAgent(
            id: Name,
            name: Name,
            description: ToolDescription,
            includeWorkflowOutputsInResponse: true);

        return workflowAgent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "process_voice_memo",
            Description = ToolDescription,
        });
    }
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
