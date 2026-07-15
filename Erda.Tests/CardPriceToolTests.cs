using System.Text.Json;
using Erda.Agents.Tools;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class CardPriceToolTests
{
    private sealed class FakeScryfall(CardResolution result) : IScryfallClient
    {
        public Exception? Throw { get; init; }
        public Task<CardResolution> ResolveAsync(string name, string? set, CancellationToken ct = default) =>
            Throw is not null ? Task.FromException<CardResolution>(Throw) : Task.FromResult(result);
    }

    private sealed class FakeCardmarket(IReadOnlyList<CardmarketOffer> offers) : ICardmarketPriceService
    {
        public string? LastUrl { get; private set; }
        public Task<IReadOnlyList<CardmarketOffer>> GetGermanOffersAsync(
            string cardPageUrl, int count, CancellationToken ct = default)
        {
            LastUrl = cardPageUrl;
            return Task.FromResult(offers);
        }
    }

    private static AIFunction Tool(IScryfallClient scryfall, ICardmarketPriceService cardmarket) =>
        (AIFunction)new CardPriceTool(scryfall, cardmarket).AsTools().Single();

    private static async Task<string> Invoke(IScryfallClient scryfall, ICardmarketPriceService cardmarket, string name, string? set = null) =>
        ((JsonElement)(await Tool(scryfall, cardmarket).InvokeAsync(new() { ["name"] = name, ["set"] = set! }))!).GetString()!;

    private static CardResolution.Match Ragavan(string? cardmarketUrl = "https://www.cardmarket.com/en/Magic/Products?idProduct=1", decimal? trend = 34.13m) =>
        new("Ragavan, Nimble Pilferer", "mh2", "Modern Horizons 2", cardmarketUrl, trend, null);

    [Fact]
    public void Exposes_exactly_the_card_price_tool()
    {
        var names = new CardPriceTool(new FakeScryfall(new CardResolution.NotFound()), new FakeCardmarket([]))
            .AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Equal(new[] { "card_price" }, names);
    }

    [Fact]
    public async Task Candidates_returns_a_did_you_mean_list_with_no_prices()
    {
        var scryfall = new FakeScryfall(new CardResolution.Candidates(["Ragavan, Nimble Pilferer", "Ragavan's Hideout"]));

        var result = await Invoke(scryfall, new FakeCardmarket([]), "ragavan");

        Assert.Contains("Ragavan, Nimble Pilferer", result);
        Assert.Contains("Ragavan's Hideout", result);
        Assert.DoesNotContain("€", result);            // no prices when disambiguating
        Assert.DoesNotContain("Trend", result);
    }

    [Fact]
    public async Task Not_found_reports_it()
    {
        var result = await Invoke(new FakeScryfall(new CardResolution.NotFound()), new FakeCardmarket([]), "zzzxxx");
        Assert.Contains("Couldn't find", result);
    }

    [Fact]
    public async Task Scryfall_error_reports_a_short_message()
    {
        var scryfall = new FakeScryfall(new CardResolution.NotFound()) { Throw = new HttpRequestException("down") };
        var result = await Invoke(scryfall, new FakeCardmarket([]), "anything");
        Assert.Contains("Scryfall", result);
    }

    [Fact]
    public async Task Prices_the_card_level_page_even_when_scryfall_has_no_product_link()
    {
        // Scryfall's per-printing purchase link may be null, but the card-level /Cards/<slug> page is
        // built from the name — so we still scrape all printings rather than bailing to trend-only.
        var cardmarket = new FakeCardmarket([new CardmarketOffer(30.00m, "NM", "s")]);

        var result = await Invoke(new FakeScryfall(Ragavan(cardmarketUrl: null)), cardmarket, "ragavan");

        Assert.Equal(
            "https://www.cardmarket.com/en/Magic/Cards/Ragavan-Nimble-Pilferer?sellerCountry=7&language=1",
            cardmarket.LastUrl);
        Assert.Contains("€30,00 · NM · s", result);
    }

    [Fact]
    public async Task Happy_path_formats_the_offer_list_with_trend_and_link()
    {
        var scryfall = new FakeScryfall(Ragavan());
        var cardmarket = new FakeCardmarket(
        [
            new CardmarketOffer(31.00m, "NM", "seller123"),
            new CardmarketOffer(31.50m, "EX", "otherseller"),
        ]);

        var result = await Invoke(scryfall, cardmarket, "Ragavan, Nimble Pilferer", set: "mh2");

        Assert.Contains("Ragavan, Nimble Pilferer (MH2) — English, DE sellers", result);
        Assert.Contains("€31,00 · NM · seller123", result);
        Assert.Contains("€31,50 · EX · otherseller", result);
        Assert.Contains("Trend: €34,13", result);
        // The tappable link is the all-printings card-level page, filtered to German sellers + English.
        Assert.Contains("/Cards/Ragavan-Nimble-Pilferer?sellerCountry=7&language=1", result);
    }

    [Fact]
    public async Task Scrape_empty_falls_back_to_trend_and_filtered_link()
    {
        var scryfall = new FakeScryfall(Ragavan());

        // No set passed → the assumed-set note is stated.
        var result = await Invoke(scryfall, new FakeCardmarket([]), "ragavan");

        Assert.Contains("Couldn't read live Cardmarket offers", result);
        Assert.Contains("Trend: €34,13", result);
        Assert.Contains("sellerCountry=7", result);
        Assert.Contains("assumed MH2", result);
    }
}
