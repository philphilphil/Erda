using Erda.Agents.Tools;

namespace Erda.Server.Api.Capabilities;

/// <summary>The <c>/api/capabilities/mcp</c> endpoint backing the Capabilities page's "Connected MCPs" panel.</summary>
public static class CapabilitiesEndpoints
{
    public static RouteGroupBuilder MapCapabilitiesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/capabilities/mcp", (IBrowserMcp mcp) => Results.Ok(BuildMcpResponse(mcp)));
        return group;
    }

    /// <summary>Pure mapping from the MCP status snapshot to the API DTO (unit-tested).</summary>
    public static McpCapabilitiesResponse BuildMcpResponse(IBrowserMcp mcp)
    {
        var s = mcp.Status;
        var servers = new List<McpServerDto>
        {
            new(s.Name, s.Transport, s.Connected, s.Tools.Select(t => new McpToolDto(t.Name, t.Description)).ToList()),
        };
        return new McpCapabilitiesResponse(servers);
    }
}
