using Microsoft.Extensions.Hosting;

namespace Erda.Agents.Tools;

/// <summary>
/// Connects the browser MCP at startup, before any agent is resolved, so the synchronous agent build
/// can read <see cref="IBrowserMcp.Tools"/>. No-op when the feature is disabled.
/// </summary>
public sealed class BrowserMcpHostedService(IBrowserMcp mcp) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => mcp.EnsureStartedAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
