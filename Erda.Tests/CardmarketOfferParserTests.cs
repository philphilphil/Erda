using Erda.Agents.Tools;
using Xunit;

namespace Erda.Tests;

public class CardmarketOfferParserTests
{
    [Fact]
    public void Parses_german_price_condition_and_seller()
    {
        var json = """[{"price":"31,00 €","condition":"NM","seller":"seller123"}]""";

        var offers = CardmarketPriceService.ParseOffers(json, 5);

        var offer = Assert.Single(offers);
        Assert.Equal(31.00m, offer.Price);
        Assert.Equal("NM", offer.Condition);
        Assert.Equal("seller123", offer.Seller);
    }

    [Fact]
    public void Parses_thousands_separated_german_price()
    {
        var json = """[{"price":"1.234,56 €","condition":"EX","seller":"x"}]""";

        var offer = Assert.Single(CardmarketPriceService.ParseOffers(json, 5));

        Assert.Equal(1234.56m, offer.Price);
    }

    [Fact]
    public void Missing_condition_and_seller_default_to_empty()
    {
        var json = """[{"price":"31,50 €"}]""";

        var offer = Assert.Single(CardmarketPriceService.ParseOffers(json, 5));

        Assert.Equal(31.50m, offer.Price);
        Assert.Equal("", offer.Condition);
        Assert.Equal("", offer.Seller);
    }

    [Fact]
    public void Rows_with_an_unparseable_price_are_skipped()
    {
        var json = """[{"price":"","seller":"a"},{"price":"2,00 €","seller":"b"}]""";

        var offer = Assert.Single(CardmarketPriceService.ParseOffers(json, 5));

        Assert.Equal(2.00m, offer.Price);
        Assert.Equal("b", offer.Seller);
    }

    [Fact]
    public void Caps_the_result_at_count()
    {
        var json = """
            [{"price":"1,00 €","seller":"a"},
             {"price":"2,00 €","seller":"b"},
             {"price":"3,00 €","seller":"c"}]
            """;

        var offers = CardmarketPriceService.ParseOffers(json, 2);

        Assert.Equal(2, offers.Count);
        Assert.Equal("a", offers[0].Seller);
        Assert.Equal("b", offers[1].Seller);
    }

    [Fact]
    public void Empty_or_garbage_json_yields_no_offers()
    {
        Assert.Empty(CardmarketPriceService.ParseOffers("", 5));
        Assert.Empty(CardmarketPriceService.ParseOffers("not json", 5));
        Assert.Empty(CardmarketPriceService.ParseOffers("[]", 5));
    }

    // The Playwright MCP wraps browser_evaluate output in a multi-section markdown blob: the array return
    // value lives in "### Result", while "### Ran Playwright code" echoes the JS source — which is full of
    // '['/']' (const out = [], [id^="articleRow"], [class*="price"]). Extraction must read only the Result
    // section, or the bracket span runs into the echoed code and yields invalid JSON.
    private const string EvaluateResponse = """
        ### Result
        [
          {
            "price": "31,00 €",
            "condition": "NM",
            "seller": "seller123"
          },
          {
            "price": "31,50 €",
            "condition": "EX",
            "seller": "otherseller"
          }
        ]
        ### Ran Playwright code
        ```js
        await page.evaluate("() => {\n  const rows = Array.from(document.querySelectorAll('.article-row, [id^=\"articleRow\"], .table-body [class*=\"row\"]'));\n  const out = [];\n  return out;\n}");
        ```
        ### Page
        - Page URL: https://www.cardmarket.com/en/Magic/Products/Singles/Modern-Horizons-2/Ragavan-Nimble-Pilferer
        ### Snapshot
        - [Snapshot](page-2026-07-15.yml)
        """;

    [Fact]
    public void Extracts_offers_from_the_full_mcp_result_blob()
    {
        var json = CardmarketPriceService.ExtractOffersJson(EvaluateResponse);
        var offers = CardmarketPriceService.ParseOffers(json, 5);

        Assert.Equal(2, offers.Count);
        Assert.Equal(31.00m, offers[0].Price);
        Assert.Equal("NM", offers[0].Condition);
        Assert.Equal("seller123", offers[0].Seller);
        Assert.Equal(31.50m, offers[1].Price);
        Assert.Equal("otherseller", offers[1].Seller);
    }

    [Fact]
    public void ExtractOffersJson_returns_empty_when_no_result_section()
    {
        // No "### Result" section (e.g. an evaluate error) — must not scavenge brackets from other sections.
        const string errorResponse = """
            ### Error
            SyntaxError: Unexpected token
            ### Ran Playwright code
            ```js
            const out = [];
            ```
            """;
        Assert.Empty(CardmarketPriceService.ParseOffers(CardmarketPriceService.ExtractOffersJson(errorResponse), 5));
    }
}
