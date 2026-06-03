using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows;

/// <summary>Workflows-tab provider for the voice-memo pipeline (see <see cref="VoiceMemoWorkflow"/>).</summary>
internal sealed class VoiceMemoWorkflowProvider : IWorkflowProvider
{
    public string Id => VoiceMemoWorkflow.Name;

    public string Title => "Voice memo";

    public string Description =>
        "Transcribe an Apple Voice Memo (.m4a), structure the transcript into a Markdown note with " +
        "Codex, and save it to the Obsidian vault.";

    public IReadOnlyList<string> Tags { get; } = ["tool: process_voice_memo", "WhatsApp voice"];

    public Workflow Build(IServiceProvider services) => VoiceMemoWorkflow.Build(services);
}
