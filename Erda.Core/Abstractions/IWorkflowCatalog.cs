namespace Erda.Core.Abstractions;

/// <summary>One step (executor) in a workflow: its id, .NET executor type, and the message types it
/// accepts/produces. <see cref="IsStart"/> marks the entry node. All values are read from the Agent
/// Framework's own workflow reflection — no hand-authored metadata.</summary>
public sealed record WorkflowNode(
    string Id, string Type, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, bool IsStart);

/// <summary>A directed connection between two nodes (by id).</summary>
public sealed record WorkflowEdge(string From, string To);

/// <summary>
/// A discovered MAF workflow as a small graph the panel draws itself: friendly header
/// (<see cref="Title"/>/<see cref="Description"/>/<see cref="Tags"/>) plus the reflected
/// <see cref="Nodes"/> (ordered from the start) and <see cref="Edges"/>. When <see cref="Runnable"/>
/// is true the panel offers a "Run" box (text in → text out), labelled by <see cref="InputLabel"/>.
/// </summary>
public sealed record WorkflowGraph(
    string Id, string Title, string Description, IReadOnlyList<string> Tags,
    IReadOnlyList<WorkflowNode> Nodes, IReadOnlyList<WorkflowEdge> Edges,
    bool Runnable, string InputLabel);

/// <summary>
/// Lists the workflows defined in the app and runs the panel-runnable ones. The implementation lives
/// in the MAF layer (it builds each workflow, reflects its graph, and executes it); this seam keeps
/// Core free of MAF.
/// </summary>
public interface IWorkflowCatalog
{
    IReadOnlyList<WorkflowGraph> GetAll();

    /// <summary>Run a panel-runnable workflow with a text input and return its text output. Throws
    /// <see cref="KeyNotFoundException"/> for an unknown id and <see cref="InvalidOperationException"/>
    /// if the workflow isn't runnable from the panel.</summary>
    Task<string> RunAsync(string id, string input, CancellationToken cancellationToken = default);
}
