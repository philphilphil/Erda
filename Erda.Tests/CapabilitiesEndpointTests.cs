using Erda.Agents.Tools;
using Erda.Server.Api.Capabilities;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class CapabilitiesEndpointTests
{
    private sealed class FakeMcp : IBrowserMcp
    {
        public bool Enabled => true;
        public IReadOnlyList<AITool> Tools => [];
        public McpServerStatus Status => new("playwright", "stdio", true,
            [new McpToolInfo("browser_navigate", "Go to a URL"), new McpToolInfo("browser_click", null)]);
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void Maps_mcp_status_to_dto()
    {
        var dto = CapabilitiesEndpoints.BuildMcpResponse(new FakeMcp());

        var server = Assert.Single(dto.Servers);
        Assert.Equal("playwright", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.True(server.Connected);
        Assert.Equal(2, server.Tools.Count);
        Assert.Equal("browser_navigate", server.Tools[0].Name);
        Assert.Equal("Go to a URL", server.Tools[0].Description);
    }

    [Fact]
    public void Maps_op_item_list_to_accounts_without_secrets()
    {
        const string listJson = """
        [ { "id":"moxid", "title":"Moxfield",
            "urls":[ { "primary":true, "href":"https://www.moxfield.com" } ] },
          { "id":"ghid", "title":"GitHub",
            "urls":[ { "href":"https://github.com" } ] } ]
        """;

        var dto = CapabilitiesEndpoints.BuildAccountsResponse(listJson);

        Assert.Equal(2, dto.Accounts.Count);
        Assert.Equal("Moxfield", dto.Accounts[0].Title);
        Assert.Equal("https://www.moxfield.com", Assert.Single(dto.Accounts[0].Sites));
        // The DTO shape physically cannot carry a value field — assert the serialized form has none.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("value", json, System.StringComparison.OrdinalIgnoreCase);
    }
}
