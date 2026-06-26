using Erda.Core.Data;
using Erda.Core.Services;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows.Executors;

/// <summary>Step 2: transcript -> structured Markdown note (the reasoner on the Responses endpoint). The
/// voice-memo prompt is read from the store (authored in the control panel); empty when none has been
/// saved yet (fresh DB).</summary>
internal sealed class CodexExecutor(IReasoner reasoner, IPromptStore prompts, ILogger<CodexExecutor> logger)
    : Executor<string, string>("codex")
{
    public override async ValueTask<string> HandleAsync(
        string transcript, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var instruction = prompts.GetActiveContent(PromptKind.Voice) ?? "";
        try
        {
            return await reasoner.RunAsync(instruction, transcript, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never lose the memo: if reasoning fails (e.g. the endpoint returns empty output), fall back
            // to saving the raw transcript so the note still lands in the inbox rather than the workflow
            // throwing and the user getting nothing.
            logger.LogWarning(ex, "Voice-memo reasoning failed; saving the raw transcript unprocessed.");
            return transcript;
        }
    }
}
