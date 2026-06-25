using Erda.Core.Services;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows.Executors;

/// <summary>Recipe importer, step 3 (terminal): readable page text → a clean Markdown recipe, via the reasoner.</summary>
internal sealed class FormatRecipeExecutor(IReasoner reasoner) : Executor<string, string>("format")
{
    private const string Instruction = """
        You are given text scraped from a recipe web page (it may include embedded JSON-LD recipe data).
        Output ONE clean Markdown document for the recipe and nothing else — no preamble, no code fences:

        # <Recipe title>

        <one short line about the dish, only if obvious>

        **Servings:** … · **Time:** …   (only the ones the page actually states)

        ## Ingredients
        - <quantity + ingredient>, one per line

        ## Preparation
        1. <clear, concise step>
        2. …

        Rules: keep the original quantities and units; ignore ads, cookie banners, navigation, blog
        backstories, and comments; if the page clearly isn't a recipe, output exactly:
        "No recipe found at that page." Match the recipe's language (German in → German out).
        """;

    public override async ValueTask<string> HandleAsync(
        string pageText, IWorkflowContext context, CancellationToken cancellationToken = default)
        => await reasoner.ReasonAsync(
            $"{Instruction}\n\n---\n{pageText}", webSearch: false, cancellationToken, logLabel: "recipe import");
}
