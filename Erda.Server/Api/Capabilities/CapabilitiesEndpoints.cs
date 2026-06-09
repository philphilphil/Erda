using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services.OnePassword;
using Microsoft.Extensions.Options;

namespace Erda.Server.Api.Capabilities;

/// <summary>The <c>/api/capabilities/mcp</c> endpoint backing the Capabilities page's "Connected MCPs" panel.</summary>
public static class CapabilitiesEndpoints
{
    public static RouteGroupBuilder MapCapabilitiesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/capabilities/mcp", (IBrowserMcp mcp) => Results.Ok(BuildMcpResponse(mcp)));

        group.MapGet("/capabilities/accounts", async (IOpCli op, IOptions<BrowserOptions> browser, CancellationToken ct) =>
        {
            var vault = browser.Value.OnePasswordVault;
            try
            {
                var json = await op.RunAsync(["item", "list", "--vault", vault, "--format", "json"], ct);
                return Results.Ok(BuildAccountsResponse(json));
            }
            catch (OpCliException)
            {
                // 1Password not configured / unreachable — show an empty list, not an error.
                return Results.Ok(new AccountsResponse([]));
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Ok(new AccountsResponse([]));
            }
        });

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

    /// <summary>Maps <c>op item list</c> JSON to the read-only accounts DTO (titles + sites only).</summary>
    public static AccountsResponse BuildAccountsResponse(string opItemListJson)
    {
        var accounts = FindLogin.ParseList(opItemListJson)
            .Select(i => new AccountDto(i.Title, i.Urls))
            .ToList();
        return new AccountsResponse(accounts);
    }
}
