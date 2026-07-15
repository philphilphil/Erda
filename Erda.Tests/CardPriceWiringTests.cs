using Erda.Agents;
using Erda.Agents.Tools;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class CardPriceWiringTests
{
    private sealed class FakeMcp(bool enabled, int toolCount) : IBrowserMcp
    {
        public bool Enabled => enabled;
        public IReadOnlyList<AITool> Tools { get; } =
            [.. Enumerable.Range(0, toolCount).Select(i => AIFunctionFactory.Create(() => "x", $"browser_tool_{i}"))];
        public McpServerStatus Status => new("playwright", "stdio", enabled, []);
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeScryfall : IScryfallClient
    {
        public Task<CardResolution> ResolveAsync(string name, string? set, CancellationToken ct = default) =>
            Task.FromResult<CardResolution>(new CardResolution.NotFound());
    }

    private sealed class FakeCardmarket : ICardmarketPriceService
    {
        public Task<IReadOnlyList<CardmarketOffer>> GetGermanOffersAsync(
            string cardPageUrl, int count, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CardmarketOffer>>([]);
    }

    private static CardPriceTool MakeTool() => new(new FakeScryfall(), new FakeCardmarket());

    // Mirrors the exact gate in ErdaAgent.Create: card_price is added only when the browser is exposed
    // (browseTool is not null == BrowserAgent.ShouldExpose(mcp)).
    private static List<string> WiredToolNames(IBrowserMcp mcp)
    {
        var names = new List<string>();
        if (BrowserAgent.ShouldExpose(mcp))
            names.AddRange(MakeTool().AsTools().Select(t => ((AIFunction)t).Name));
        return names;
    }

    [Fact]
    public void CardPriceTool_exposes_exactly_card_price()
    {
        var names = MakeTool().AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Equal(new[] { "card_price" }, names);
    }

    [Fact]
    public void Card_price_present_when_browser_exposed()
        => Assert.Contains("card_price", WiredToolNames(new FakeMcp(enabled: true, toolCount: 3)));

    [Fact]
    public void Card_price_absent_when_browser_disabled()
        => Assert.DoesNotContain("card_price", WiredToolNames(new FakeMcp(enabled: false, toolCount: 3)));

    [Fact]
    public void Card_price_absent_when_browser_has_no_tools()
        => Assert.DoesNotContain("card_price", WiredToolNames(new FakeMcp(enabled: true, toolCount: 0)));
}
