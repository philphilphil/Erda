using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows;

/// <summary>Workflows-tab provider for the recipe importer (see <see cref="RecipeWorkflow"/>): runnable
/// from the panel, not a chat tool.</summary>
internal sealed class RecipeWorkflowProvider : IWorkflowProvider
{
    public string Id => RecipeWorkflow.Name;

    public string Title => "Recipe importer";

    public string Description =>
        "Paste a recipe link; Erda fetches the page and returns a clean Markdown recipe — ingredients " +
        "up top, then easy step-by-step preparation. Run it here; it isn't a chat tool.";

    public IReadOnlyList<string> Tags { get; } = ["web only", "fetches a URL", "Codex"];

    public bool RunnableFromPanel => true;

    public string InputLabel => "Recipe URL";

    public Workflow Build(IServiceProvider services) => RecipeWorkflow.Build(services);
}
