using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Erda.Agents.Workflows.Executors;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Agents.Workflows;

/// <summary>
/// The "voice-memo" MAF workflow: a chat-protocol input adapter plus a three-step chain.
///
///   Input (chat -> path) -> Transcribe (.m4a -> text) -> Codex (text -> note) -> ObsidianWrite (note -> ChatMessage)
///
/// The three middle steps are plain string executors (Codex is shelled out, not an AIAgent
/// node). But when a workflow is hosted as an AIAgent (here: wrapped as the process_voice_memo
/// tool via AsAIFunction), its START executor must speak the chat protocol (accept
/// List&lt;ChatMessage&gt; + TurnToken) and its OUTPUT must be a ChatMessage to surface in the
/// response. So the chain is bookended by
/// <see cref="VoiceMemoInputExecutor"/> (chat -> path string) at the front and a
/// ChatMessage-returning <see cref="ObsidianWriteExecutor"/> at the end.
/// </summary>
public static class VoiceMemoWorkflow
{
    public const string Name = "voice-memo";

    public static Workflow Build(IServiceProvider services)
    {
        var input = new VoiceMemoInputExecutor();
        var transcribe = new TranscribeExecutor(services.GetRequiredService<Transcriber>());
        var codex = new CodexExecutor(
            services.GetRequiredService<CodexRunner>(),
            services.GetRequiredService<IPromptStore>());
        var write = new ObsidianWriteExecutor(
            services.GetRequiredService<VaultService>(),
            services.GetRequiredService<IOptions<ErdaOptions>>().Value);

        return new WorkflowBuilder(input)
            .AddEdge(input, transcribe)
            .AddEdge(transcribe, codex)
            .AddEdge(codex, write)
            .WithOutputFrom(write)
            .WithName(Name) // workflow name, surfaced when it's wrapped as the process_voice_memo tool
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
