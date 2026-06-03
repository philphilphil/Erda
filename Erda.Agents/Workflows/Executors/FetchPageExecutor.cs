using Erda.Core.Services;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows.Executors;

/// <summary>Recipe importer, step 1: a recipe URL → the raw page HTML (HTTP GET).</summary>
internal sealed class FetchPageExecutor(IUrlFetcher fetcher) : Executor<string, string>("fetch")
{
    public override async ValueTask<string> HandleAsync(
        string url, IWorkflowContext context, CancellationToken cancellationToken = default)
        => await fetcher.FetchAsync(url, cancellationToken);
}
