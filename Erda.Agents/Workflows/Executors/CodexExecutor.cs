using Erda.Core.Services;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows.Executors;

/// <summary>Step 2: transcript -> structured Markdown note (Codex on the ChatGPT subscription).</summary>
internal sealed class CodexExecutor(CodexRunner codex)
    : Executor<string, string>("codex")
{
    public override async ValueTask<string> HandleAsync(
        string transcript, IWorkflowContext context, CancellationToken cancellationToken = default)
        => await codex.RunAsync(VoiceMemoWorkflow.DeveloperInstruction, transcript, cancellationToken);
}
