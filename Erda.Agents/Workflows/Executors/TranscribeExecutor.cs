using Erda.Services;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Workflows.Executors;

/// <summary>Step 1: absolute .m4a path -> transcript text (OpenAI platform key).</summary>
internal sealed class TranscribeExecutor(Transcriber transcriber)
    : Executor<string, string>("transcribe")
{
    public override async ValueTask<string> HandleAsync(
        string m4aPath, IWorkflowContext context, CancellationToken cancellationToken = default)
        => await transcriber.TranscribeAsync(m4aPath.Trim(), cancellationToken);
}
