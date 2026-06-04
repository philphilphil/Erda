using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>One tool exposed by an MCP server, for the capabilities panel.</summary>
public sealed record McpToolInfo(string Name, string? Description);

/// <summary>A point-in-time snapshot of an MCP server connection, for the capabilities panel.</summary>
public sealed record McpServerStatus(string Name, string Transport, bool Connected, IReadOnlyList<McpToolInfo> Tools);

/// <summary>
/// Owns the lifecycle of the Playwright MCP server (a stdio child process) and exposes its tools to
/// the browser sub-agent. A single instance; connected once at startup (Program.cs calls
/// <see cref="EnsureStartedAsync"/> before the host starts, so the orchestrator agent sees the
/// tools when it is built). When the feature is disabled, this is a no-op:
/// <see cref="Tools"/> is empty and no child process is launched.
/// </summary>
public interface IBrowserMcp
{
    bool Enabled { get; }

    /// <summary>The MCP tools as MAF <see cref="AITool"/>s (empty until connected, or if disabled/failed).</summary>
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>Snapshot for the capabilities panel.</summary>
    McpServerStatus Status { get; }

    /// <summary>Idempotent connect. Safe to call when disabled (no-op). Never throws — failures leave Tools empty and Connected=false.</summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);
}
