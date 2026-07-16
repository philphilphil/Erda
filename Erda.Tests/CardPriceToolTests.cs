using System.Text.Json;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class CardPriceToolTests
{
    private sealed class FakeScryfall(CardResolution result) : IScryfallClient
    {
        public Exception? Throw { get; init; }
        public bool DownloadSucceeds { get; init; } = true;
        public string? LastImageUrl { get; private set; }
        public string? LastImagePath { get; private set; }

        public Task<CardResolution> ResolveAsync(string name, string? set, CancellationToken ct = default) =>
            Throw is not null ? Task.FromException<CardResolution>(Throw) : Task.FromResult(result);

        public Task<bool> TryDownloadImageAsync(string imageUrl, string destinationPath, CancellationToken ct = default)
        {
            LastImageUrl = imageUrl;
            LastImagePath = destinationPath;
            return Task.FromResult(DownloadSucceeds);
        }
    }

    private static CardPriceTool Tool(IScryfallClient scryfall, string mediaTempDir = "/media") =>
        new(scryfall, Options.Create(new WhatsAppOptions { MediaTempDir = mediaTempDir }));

    private static async Task<string> Invoke(CardPriceTool tool, string name, string? set = null)
    {
        var fn = (AIFunction)tool.AsTools().Single();
        return ((JsonElement)(await fn.InvokeAsync(new() { ["name"] = name, ["set"] = set! }))!).GetString()!;
    }

    private static CardResolution.Match Ragavan(
        decimal? trend = 34.13m, string? imageUrl = "https://cards.scryfall.io/normal/ragavan.jpg") =>
        new("Ragavan, Nimble Pilferer", "mh2", "Modern Horizons 2",
            "https://www.cardmarket.com/en/Magic/Products?idProduct=1", trend, imageUrl);

    [Fact]
    public void Exposes_exactly_the_card_price_tool()
    {
        var names = Tool(new FakeScryfall(new CardResolution.NotFound()))
            .AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Equal(new[] { "card_price" }, names);
    }

    [Fact]
    public async Task Candidates_returns_a_did_you_mean_list_with_no_prices()
    {
        var tool = Tool(new FakeScryfall(new CardResolution.Candidates(["Ragavan, Nimble Pilferer", "Ragavan's Hideout"])));

        var result = await Invoke(tool, "ragavan");

        Assert.Contains("Ragavan, Nimble Pilferer", result);
        Assert.Contains("Ragavan's Hideout", result);
        Assert.DoesNotContain("€", result);            // no prices when disambiguating
        Assert.DoesNotContain("Trend", result);
    }

    [Fact]
    public async Task Not_found_reports_it()
    {
        var result = await Invoke(Tool(new FakeScryfall(new CardResolution.NotFound())), "zzzxxx");
        Assert.Contains("Couldn't find", result);
    }

    [Fact]
    public async Task Scryfall_error_reports_a_short_message()
    {
        var scryfall = new FakeScryfall(new CardResolution.NotFound()) { Throw = new HttpRequestException("down") };
        var result = await Invoke(Tool(scryfall), "anything");
        Assert.Contains("Scryfall", result);
    }

    [Fact]
    public async Task Match_returns_trend_link_and_image()
    {
        var scryfall = new FakeScryfall(Ragavan());

        var result = await Invoke(Tool(scryfall), "Ragavan, Nimble Pilferer", set: "mh2");

        Assert.Contains("Ragavan, Nimble Pilferer (MH2 — Modern Horizons 2)", result);
        Assert.Contains("Trend: €34,13", result);
        Assert.DoesNotContain("Foil", result);
        // The tappable link: all-printings card page, German sellers, English.
        Assert.Contains("/Cards/Ragavan-Nimble-Pilferer?sellerCountry=7&language=1", result);
        // Image downloaded into the media dir and handed to the orchestrator for send_image.
        Assert.Contains("send_image", result);
        Assert.Equal("https://cards.scryfall.io/normal/ragavan.jpg", scryfall.LastImageUrl);
        Assert.Equal(Path.Combine("/media", "card-ragavan-nimble-pilferer.jpg"), scryfall.LastImagePath);
        Assert.Contains(scryfall.LastImagePath!, result);
        // Set was pinned → no assumed-set note.
        Assert.DoesNotContain("say the set", result);
    }

    [Fact]
    public async Task Without_a_set_the_assumed_printing_is_stated()
    {
        var result = await Invoke(Tool(new FakeScryfall(Ragavan())), "ragavan");
        Assert.Contains("Cheapest printing: MH2", result);
    }

    [Fact]
    public async Task No_image_or_no_media_dir_or_failed_download_degrades_to_text_only()
    {
        // No image URL on the match.
        var noImage = await Invoke(Tool(new FakeScryfall(Ragavan(imageUrl: null))), "ragavan");
        Assert.DoesNotContain("send_image", noImage);

        // No media dir configured (WhatsApp off).
        var noDir = await Invoke(Tool(new FakeScryfall(Ragavan()), mediaTempDir: ""), "ragavan");
        Assert.DoesNotContain("send_image", noDir);

        // Download fails.
        var failed = await Invoke(Tool(new FakeScryfall(Ragavan()) { DownloadSucceeds = false }), "ragavan");
        Assert.DoesNotContain("send_image", failed);
        Assert.Contains("Trend: €34,13", failed); // the price still comes back
    }

    [Fact]
    public async Task Missing_trend_is_reported()
    {
        var result = await Invoke(Tool(new FakeScryfall(Ragavan(trend: null))), "ragavan");
        Assert.Contains("No EUR trend price", result);
    }
}
