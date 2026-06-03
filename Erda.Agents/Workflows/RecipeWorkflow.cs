using Erda.Agents.Workflows.Executors;
using Erda.Core.Services;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Erda.Agents.Workflows;

/// <summary>
/// The "recipe importer" workflow: a recipe URL → a clean Markdown recipe. Web-only — it's run from
/// the control panel and is deliberately NOT exposed as an agent tool.
///
///   Fetch (url → HTML) → Extract (HTML → readable text) → Format (text → Markdown, via Codex)
/// </summary>
public static class RecipeWorkflow
{
    public const string Name = "recipe";

    public static Workflow Build(IServiceProvider services)
    {
        var fetch = new FetchPageExecutor(services.GetRequiredService<IUrlFetcher>());
        var extract = new ExtractRecipeTextExecutor();
        var format = new FormatRecipeExecutor(services.GetRequiredService<ICodexRunner>());

        return new WorkflowBuilder(fetch)
            .AddEdge(fetch, extract)
            .AddEdge(extract, format)
            .WithOutputFrom(format)
            .WithName(Name)
            .Build();
    }
}
