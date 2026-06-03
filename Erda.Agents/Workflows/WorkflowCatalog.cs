using Erda.Core.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows;

/// <summary>
/// Builds each discovered <see cref="IWorkflowProvider"/>'s workflow and reflects its graph (nodes +
/// edges) via the Agent Framework's own workflow introspection — so the diagram always matches the
/// real workflow, with no hand-authored step metadata. Built once and cached (a workflow's structure
/// is static) and lazily, so nothing is constructed during app startup.
/// </summary>
internal sealed class WorkflowCatalog(IServiceProvider services, IEnumerable<IWorkflowProvider> providers)
    : IWorkflowCatalog
{
    private readonly Lazy<IReadOnlyList<WorkflowGraph>> _graphs = new(() => Build(services, providers));

    public IReadOnlyList<WorkflowGraph> GetAll() => _graphs.Value;

    public async Task<string> RunAsync(string id, string input, CancellationToken cancellationToken = default)
    {
        var provider = providers.FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException($"No workflow '{id}'.");
        if (!provider.RunnableFromPanel)
            throw new InvalidOperationException($"Workflow '{id}' can't be run from the panel.");

        // Build a fresh instance per run (a run carries state) and execute it to completion.
        var workflow = provider.Build(services);
        var run = await InProcessExecution.RunAsync(workflow, input, cancellationToken: cancellationToken);
        return run.OutgoingEvents.OfType<WorkflowOutputEvent>().LastOrDefault()?.Data?.ToString() ?? "";
    }

    private static IReadOnlyList<WorkflowGraph> Build(IServiceProvider services, IEnumerable<IWorkflowProvider> providers)
        => providers
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .Select(p => ToGraph(p, services))
            .ToList();

    private static WorkflowGraph ToGraph(IWorkflowProvider provider, IServiceProvider services)
    {
        var workflow = provider.Build(services);
        var start = workflow.StartExecutorId;

        var nodes = workflow.ReflectExecutors().Select(kvp =>
        {
            IReadOnlyList<string> inputs = [];
            IReadOnlyList<string> outputs = [];
            if (kvp.Value is ExecutorInstanceBinding instance)
            {
                inputs = instance.ExecutorInstance.InputTypes.Select(Friendly).Distinct().ToList();
                outputs = instance.ExecutorInstance.OutputTypes.Select(Friendly).Distinct().ToList();
            }
            return new WorkflowNode(kvp.Key, kvp.Value.ExecutorType.Name, inputs, outputs, kvp.Key == start);
        }).ToList();

        var edges = (from kvp in workflow.ReflectEdges()
                     from edge in kvp.Value
                     from source in edge.Connection.SourceIds
                     from sink in edge.Connection.SinkIds
                     select new WorkflowEdge(source, sink)).ToList();

        return new WorkflowGraph(
            provider.Id, provider.Title, provider.Description, provider.Tags,
            OrderFromStart(nodes, edges, start), edges,
            provider.RunnableFromPanel, provider.InputLabel);
    }

    /// <summary>Order nodes left→right along the flow from the start node (breadth-first), with any
    /// nodes unreachable from the start appended at the end.</summary>
    private static IReadOnlyList<WorkflowNode> OrderFromStart(
        List<WorkflowNode> nodes, List<WorkflowEdge> edges, string start)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var next = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());
        var ordered = new List<WorkflowNode>();
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        if (byId.ContainsKey(start)) queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id) || !byId.TryGetValue(id, out var node))
                continue;
            ordered.Add(node);
            if (next.TryGetValue(id, out var tos))
                foreach (var to in tos)
                    queue.Enqueue(to);
        }
        foreach (var node in nodes)
            if (seen.Add(node.Id))
                ordered.Add(node);
        return ordered;
    }

    /// <summary>A short, readable name for a message type (e.g. <c>string</c>, <c>ChatMessage[]</c>).</summary>
    private static string Friendly(Type t)
    {
        if (t.IsArray)
            return Friendly(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
            return t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Friendly)) + ">";
        return t.Name switch
        {
            "String" => "string",
            "Int32" => "int",
            "Int64" => "long",
            "Boolean" => "bool",
            "Double" => "double",
            "Single" => "float",
            "Object" => "object",
            _ => t.Name,
        };
    }
}
