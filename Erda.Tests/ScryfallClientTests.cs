using System.Net;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Erda.Tests;

public class ScryfallClientTests
{
    // Routes each Scryfall request to a canned (status, body) based on the request URL, and records
    // the URLs so a test can assert the query the client sent.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<string, (HttpStatusCode Status, string Body)> Respond { get; set; } = _ => (HttpStatusCode.NotFound, "");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var (status, body) = Respond(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ScryfallClient Make(RoutingHandler handler) =>
        new(new FakeFactory(handler), NullLogger<ScryfallClient>.Instance);

    private static string CardJson(string name, string set, string setName, string? eur, string? cardmarketUrl)
    {
        var prices = eur is null ? "null" : $"{{\"eur\":\"{eur}\",\"eur_foil\":null}}";
        var purchase = cardmarketUrl is null ? "{}" : $"{{\"cardmarket\":\"{cardmarketUrl}\"}}";
        return $$"""
            {"object":"card","name":"{{name}}","set":"{{set}}","set_name":"{{setName}}",
             "prices":{{prices}},"purchase_uris":{{purchase}}}
            """;
    }

    private static string SearchJson(params string[] names)
    {
        var data = string.Join(",", names.Select(n => $"{{\"object\":\"card\",\"name\":\"{n}\"}}"));
        return $"{{\"object\":\"list\",\"data\":[{data}]}}";
    }

    [Fact]
    public async Task Exact_hit_returns_a_match_with_trend_and_cardmarket_url()
    {
        var handler = new RoutingHandler
        {
            Respond = url => url.Contains("exact=")
                ? (HttpStatusCode.OK, CardJson("Ragavan, Nimble Pilferer", "mh2", "Modern Horizons 2",
                    "34.13", "https://www.cardmarket.com/en/Magic/Products?idProduct=1"))
                : (HttpStatusCode.NotFound, ""),
        };

        var result = await Make(handler).ResolveAsync("Ragavan, Nimble Pilferer", null);

        var match = Assert.IsType<CardResolution.Match>(result);
        Assert.Equal("Ragavan, Nimble Pilferer", match.Name);
        Assert.Equal("mh2", match.SetCode);
        Assert.Equal(34.13m, match.EurTrend);
        Assert.Contains("idProduct=1", match.CardmarketUrl);
    }

    [Fact]
    public async Task Set_param_is_forwarded_to_the_exact_query()
    {
        var handler = new RoutingHandler
        {
            Respond = url => url.Contains("exact=")
                ? (HttpStatusCode.OK, CardJson("Sol Ring", "c21", "Commander 2021", "1.50",
                    "https://www.cardmarket.com/en/Magic/Products?idProduct=2"))
                : (HttpStatusCode.NotFound, ""),
        };

        await Make(handler).ResolveAsync("Sol Ring", "c21");

        Assert.Contains(handler.Requests, r => r.Contains("exact=") && r.Contains("set=c21"));
    }

    [Fact]
    public async Task Exact_404_then_fuzzy_hit_returns_a_match()
    {
        var handler = new RoutingHandler
        {
            Respond = url => url.Contains("fuzzy=")
                ? (HttpStatusCode.OK, CardJson("Lightning Bolt", "lea", "Limited Edition Alpha", "80.00",
                    "https://www.cardmarket.com/en/Magic/Products?idProduct=3"))
                : (HttpStatusCode.NotFound, ""), // exact 404
        };

        var result = await Make(handler).ResolveAsync("lightnig bolt", null);

        var match = Assert.IsType<CardResolution.Match>(result);
        Assert.Equal("Lightning Bolt", match.Name);
        Assert.Contains("idProduct=3", match.CardmarketUrl);
    }

    [Fact]
    public async Task Fuzzy_hit_without_cardmarket_link_falls_through_to_candidates()
    {
        var handler = new RoutingHandler
        {
            Respond = url =>
            {
                if (url.Contains("exact=")) return (HttpStatusCode.NotFound, "");
                if (url.Contains("fuzzy=")) return (HttpStatusCode.OK,
                    CardJson("Some Token", "tok", "Tokens", null, null)); // real card, no CM link
                return (HttpStatusCode.OK, SearchJson("Goblin Token", "Soldier Token"));
            },
        };

        var result = await Make(handler).ResolveAsync("token", null);

        var candidates = Assert.IsType<CardResolution.Candidates>(result);
        Assert.Equal(new[] { "Goblin Token", "Soldier Token" }, candidates.Names);
    }

    [Fact]
    public async Task Search_only_returns_unique_candidate_names()
    {
        var handler = new RoutingHandler
        {
            Respond = url => url.Contains("/cards/search")
                ? (HttpStatusCode.OK, SearchJson("Llanowar Elves", "Llanowar Elves", "Fyndhorn Elves"))
                : (HttpStatusCode.NotFound, ""), // exact + fuzzy 404
        };

        var result = await Make(handler).ResolveAsync("elves", null);

        var candidates = Assert.IsType<CardResolution.Candidates>(result);
        Assert.Equal(new[] { "Llanowar Elves", "Fyndhorn Elves" }, candidates.Names); // deduped
    }

    [Fact]
    public async Task Nothing_found_returns_not_found()
    {
        var handler = new RoutingHandler { Respond = _ => (HttpStatusCode.NotFound, "") };

        var result = await Make(handler).ResolveAsync("zzzxxx not a card", null);

        Assert.IsType<CardResolution.NotFound>(result);
    }
}
