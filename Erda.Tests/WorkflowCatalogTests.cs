using Erda.Agents;
using Erda.Agents.Workflows;
using Erda.Agents.Workflows.Executors;
using Erda.Core.Abstractions;
using Erda.Core.Services;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for the Workflows-tab catalog: reflection discovery of providers and the Mermaid emitted by
/// MAF's WorkflowVisualizer. Uses a trivial fake workflow so no Erda services are needed.
/// </summary>
public class WorkflowCatalogTests
{
    [Fact]
    public void Reflects_nodes_and_edges_per_provider()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new WorkflowCatalog(sp, [new FakeProvider()]);

        var wf = Assert.Single(catalog.GetAll());

        Assert.Equal("demo", wf.Id);
        Assert.Equal("Demo", wf.Title);

        // Nodes are ordered from the start (a → b) and carry their type + I/O message types.
        Assert.Equal(["a", "b"], wf.Nodes.Select(n => n.Id));
        var a = wf.Nodes[0];
        Assert.True(a.IsStart);
        Assert.Equal("Step", a.Type);
        Assert.Equal(["string"], a.Inputs);
        Assert.Equal(["string"], a.Outputs);

        var edge = Assert.Single(wf.Edges);
        Assert.Equal("a", edge.From);
        Assert.Equal("b", edge.To);
    }

    [Fact]
    public void AddErdaAgents_auto_discovers_the_providers()
    {
        var services = new ServiceCollection();
        services.AddErdaAgents();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IWorkflowProvider) &&
            d.ImplementationType == typeof(RecipeWorkflowProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(IWorkflowCatalog));
    }

    [Fact]
    public async Task Recipe_workflow_runs_a_url_into_markdown()
    {
        var fetcher = new FakeUrlFetcher
        {
            Html = "<html><head><script type=\"application/ld+json\">{\"@type\":\"Recipe\",\"name\":\"Tomato Soup\"}</script>" +
                   "</head><body><h1>Tomato Soup</h1><p>Cozy and quick.</p></body></html>",
        };
        var reasoner = new FakeReasoner { Result = "# Tomato Soup\n## Ingredients\n- tomatoes\n## Preparation\n1. Simmer." };
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services
            .AddSingleton<IUrlFetcher>(fetcher)
            .AddSingleton<IReasoner>(reasoner)
            .BuildServiceProvider();
        var catalog = new WorkflowCatalog(sp, [new RecipeWorkflowProvider()]);

        var output = await catalog.RunAsync("recipe", "https://example.com/soup");

        Assert.Equal(reasoner.Result, output);
        Assert.Equal("https://example.com/soup", Assert.Single(fetcher.Urls));
        // The fetched page (JSON-LD + visible text) reached the reasoner.
        Assert.Contains("structured recipe data", reasoner.Calls[0].Prompt);
        Assert.Contains("Tomato Soup", reasoner.Calls[0].Prompt);
        Assert.False(reasoner.Calls[0].WebSearch); // we already have the page; no search needed
    }

    [Fact]
    public async Task Non_runnable_workflow_is_rejected()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new WorkflowCatalog(sp, [new FakeProvider()]); // not runnable from the panel

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RunAsync("demo", "x"));
    }

    [Fact]
    public async Task Unknown_workflow_run_throws_key_not_found()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new WorkflowCatalog(sp, [new FakeProvider()]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.RunAsync("nope", "x"));
    }

    [Fact]
    public void Extract_keeps_recipe_jsonld_and_strips_markup()
    {
        const string html =
            "<html><head><style>.x{}</style>" +
            "<script type=\"application/ld+json\">{\"@type\":\"Recipe\",\"name\":\"Soup\"}</script>" +
            "<script>track();</script></head>" +
            "<body><h1>Soup</h1><p>Boil&nbsp;water.</p></body></html>";

        var text = ExtractRecipeTextExecutor.Clean(html);

        Assert.Contains("structured recipe data", text);
        Assert.Contains("\"@type\":\"Recipe\"", text);   // JSON-LD kept
        Assert.Contains("Boil water.", text);            // entity decoded, tags stripped
        Assert.DoesNotContain("track()", text);          // plain <script> dropped
        Assert.DoesNotContain("<h1>", text);
    }

    [Fact]
    public void Real_recipe_workflow_diagrams_its_three_steps()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services
            .AddSingleton<IUrlFetcher>(new FakeUrlFetcher())
            .AddSingleton<IReasoner>(new FakeReasoner())
            .BuildServiceProvider();

        var graph = Assert.Single(new WorkflowCatalog(sp, [new RecipeWorkflowProvider()]).GetAll());

        Assert.Equal("recipe", graph.Id);
        Assert.Equal(["fetch", "extract", "format"], graph.Nodes.Select(n => n.Id));
        Assert.True(graph.Nodes[0].IsStart);
        Assert.Equal(2, graph.Edges.Count); // fetch→extract→format
    }

    private sealed class FakeProvider : IWorkflowProvider
    {
        public string Id => "demo";
        public string Title => "Demo";
        public string Description => "A demo workflow.";
        public IReadOnlyList<string> Tags { get; } = [];

        public Workflow Build(IServiceProvider services)
        {
            var a = new Step("a");
            var b = new Step("b");
            return new WorkflowBuilder(a).AddEdge(a, b).WithOutputFrom(b).WithName("demo").Build();
        }
    }

    private sealed class Step(string id) : Executor<string, string>(id)
    {
        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => new(message);
    }
}
