using Erda.Core.Abstractions;

namespace Erda.Server.Api;

/// <summary>
/// Read-only listing of the app's MAF workflows for the panel's Workflows tab. Each entry is a small
/// graph (nodes + edges) reflected from the real workflow; the SPA draws the diagram itself.
/// </summary>
public static class WorkflowEndpoints
{
    public static RouteGroupBuilder MapWorkflowEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/workflows");

        g.MapGet("", (IWorkflowCatalog catalog) =>
            Results.Ok(new WorkflowsResponse(catalog.GetAll())));

        // Run a panel-runnable workflow (text in → text out). May take a while (fetch + Codex), so
        // this is a plain request/response — the SPA shows a spinner.
        g.MapPost("/{id}/run", async (string id, RunWorkflowRequest req, IWorkflowCatalog catalog, CancellationToken ct) =>
        {
            var input = req.Input?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(input))
                return Results.BadRequest(new ErrorResponse("Input is required."));
            try
            {
                var output = await catalog.RunAsync(id, input, ct);
                return Results.Ok(new RunWorkflowResponse(output));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                // Surface the failure (bad URL, not runnable, Codex/launch error) to the panel.
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        });

        return group;
    }
}

/// <summary>The discovered workflows, each as a node/edge graph.</summary>
public sealed record WorkflowsResponse(IReadOnlyList<WorkflowGraph> Workflows);

/// <summary>Request to run a panel-runnable workflow.</summary>
public sealed record RunWorkflowRequest(string? Input);

/// <summary>The workflow's text output.</summary>
public sealed record RunWorkflowResponse(string Output);
