using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows;

/// <summary>
/// Describes one MAF workflow for the panel's Workflows tab: friendly metadata plus a way to build
/// the actual <see cref="Workflow"/> (which the Agent Framework then diagrams). Implementations are
/// auto-discovered by reflection in <c>AddErdaAgents</c>, so a new workflow shows up in the panel
/// just by adding a provider — no registry edit.
/// </summary>
internal interface IWorkflowProvider
{
    /// <summary>Stable key (matches the workflow's name).</summary>
    string Id { get; }

    /// <summary>Human title shown as the card heading.</summary>
    string Title { get; }

    /// <summary>One-line description of what the workflow does.</summary>
    string Description { get; }

    /// <summary>Short badges (e.g. how it's triggered).</summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>Build the workflow graph (no execution) so it can be diagrammed or run.</summary>
    Workflow Build(IServiceProvider services);

    /// <summary>When true, the panel offers a "Run" box that executes this workflow (text in → text
    /// out). Opt-in per workflow — a text-shaped workflow isn't necessarily safe/cheap to fire.</summary>
    bool RunnableFromPanel => false;

    /// <summary>Label/placeholder for the run input box.</summary>
    string InputLabel => "Input";
}
