using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erda.Core.Services;

/// <summary>
/// The outcome of resolving a (possibly garbled) card name on Scryfall. A discriminated result: a
/// single confident <see cref="Match"/>, an ambiguous set of <see cref="Candidates"/> for the
/// orchestrator to disambiguate with Phil, or <see cref="NotFound"/>.
/// </summary>
public abstract record CardResolution
{
    private CardResolution() { }

    /// <summary>A single confident printing. <see cref="CardmarketUrl"/> is null when the print has
    /// no Cardmarket product link; the EUR trend prices are null when Scryfall has no price.</summary>
    public sealed record Match(
        string Name,
        string SetCode,
        string SetName,
        string? CardmarketUrl,
        decimal? EurTrend,
        decimal? EurFoilTrend) : CardResolution;

    /// <summary>Ambiguous — the top few candidate card names, returned so the orchestrator can ask
    /// Phil which one he means before calling again.</summary>
    public sealed record Candidates(IReadOnlyList<string> Names) : CardResolution;

    /// <summary>No match and no candidates.</summary>
    public sealed record NotFound : CardResolution;
}

/// <summary>Resolves a card name to its exact printing (and Cardmarket product URL + EUR trend) on Scryfall.</summary>
public interface IScryfallClient
{
    /// <summary>
    /// Resolve <paramref name="name"/> (optionally pinned to <paramref name="set"/>) via exact → fuzzy →
    /// search. Returns a <see cref="CardResolution"/>. Throws only on a Scryfall transport/HTTP error
    /// (so the caller can surface "couldn't reach Scryfall"); a plain miss is <see cref="CardResolution.NotFound"/>.
    /// </summary>
    Task<CardResolution> ResolveAsync(string name, string? set, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IScryfallClient"/> over <see cref="IHttpClientFactory"/> (same named-client pattern as
/// <see cref="UrlFetcher"/>): a descriptive User-Agent + Accept header (Scryfall asks API clients to
/// identify themselves) and a ~100 ms throttle between requests (Scryfall's rate-limit guidance).
///
/// Resolution walks three Scryfall endpoints in order and stops at the first confident answer:
/// <list type="number">
///   <item><c>/cards/named?exact=</c> (+<c>&amp;set=</c>) — an exact hit is a <see cref="CardResolution.Match"/>.</item>
///   <item><c>/cards/named?fuzzy=</c> — a fuzzy hit is a <see cref="CardResolution.Match"/> only when it is a
///     real card <b>with</b> a Cardmarket product link (fuzzy can otherwise land on the wrong print).</item>
///   <item><c>/cards/search?q=</c> — any results become <see cref="CardResolution.Candidates"/>; none means
///     <see cref="CardResolution.NotFound"/>.</item>
/// </list>
/// </summary>
public sealed class ScryfallClient(IHttpClientFactory httpClientFactory, ILogger<ScryfallClient> logger) : IScryfallClient
{
    private const string BaseUrl = "https://api.scryfall.com";
    private const int MaxCandidates = 8;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Serializes the ~100 ms throttle across concurrent callers (this is an app-lifetime singleton).
    private readonly SemaphoreSlim _throttleGate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public async Task<CardResolution> ResolveAsync(string name, string? set, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return new CardResolution.NotFound();

        using var client = httpClientFactory.CreateClient(nameof(ScryfallClient));
        client.BaseAddress = new Uri(BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(20);
        // A descriptive User-Agent + Accept (Scryfall asks clients to identify themselves and set Accept).
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Erda/1.0 (personal Magic price assistant)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json;q=0.9,*/*;q=0.8");

        // 1. Exact — the confident path.
        var setQuery = string.IsNullOrWhiteSpace(set) ? "" : $"&set={Uri.EscapeDataString(set.Trim())}";
        var (exactStatus, exactCard) = await GetCardAsync(
            client, $"/cards/named?exact={Uri.EscapeDataString(trimmed)}{setQuery}", ct);
        if (exactStatus == HttpStatusCode.OK && exactCard is not null)
            return ToMatch(exactCard);

        // 2. Fuzzy — forgiving of misheard names, but only trusted when it lands on a real card with a
        //    Cardmarket link (fuzzy can otherwise resolve to the wrong printing / a token with no CM page).
        var (fuzzyStatus, fuzzyCard) = await GetCardAsync(
            client, $"/cards/named?fuzzy={Uri.EscapeDataString(trimmed)}", ct);
        if (fuzzyStatus == HttpStatusCode.OK && fuzzyCard is { Object: "card" } &&
            !string.IsNullOrWhiteSpace(fuzzyCard.PurchaseUris?.Cardmarket))
            return ToMatch(fuzzyCard);

        // 3. Search — ambiguous; hand back the candidate names for Phil to disambiguate.
        var names = await SearchNamesAsync(client, trimmed, ct);
        return names.Count > 0 ? new CardResolution.Candidates(names) : new CardResolution.NotFound();
    }

    private static CardResolution.Match ToMatch(ScryfallCard card) => new(
        Name: card.Name ?? "",
        SetCode: card.Set ?? "",
        SetName: card.SetName ?? "",
        CardmarketUrl: string.IsNullOrWhiteSpace(card.PurchaseUris?.Cardmarket) ? null : card.PurchaseUris!.Cardmarket,
        EurTrend: ParsePrice(card.Prices?.Eur),
        EurFoilTrend: ParsePrice(card.Prices?.EurFoil));

    /// <summary>GET a single card. 200 → the card; 404 → (404, null) so the caller can fall through;
    /// any other non-success is a transport error and throws.</summary>
    private async Task<(HttpStatusCode Status, ScryfallCard? Card)> GetCardAsync(
        HttpClient client, string relativeUrl, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        using var response = await client.GetAsync(relativeUrl, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (HttpStatusCode.NotFound, null);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Couldn't reach Scryfall (HTTP {(int)response.StatusCode}).");

        var body = await response.Content.ReadAsStringAsync(ct);
        var card = JsonSerializer.Deserialize<ScryfallCard>(body, Json);
        return (HttpStatusCode.OK, card);
    }

    /// <summary>Search for candidate names. 404 (Scryfall's "no cards found") → empty; other non-success throws.</summary>
    private async Task<IReadOnlyList<string>> SearchNamesAsync(HttpClient client, string name, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        // Scryfall's default search order is roughly relevance-ranked, which is what we want here.
        using var response = await client.GetAsync($"/cards/search?q={Uri.EscapeDataString(name)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Couldn't reach Scryfall (HTTP {(int)response.StatusCode}).");

        var body = await response.Content.ReadAsStringAsync(ct);
        var search = JsonSerializer.Deserialize<ScryfallSearch>(body, Json);
        if (search?.Data is not { Count: > 0 } data)
            return [];

        // Unique card names, in Scryfall's order, capped.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var card in data)
        {
            var cardName = card.Name?.Trim();
            if (string.IsNullOrEmpty(cardName) || !seen.Add(cardName))
                continue;
            names.Add(cardName);
            if (names.Count >= MaxCandidates)
                break;
        }
        return names;
    }

    /// <summary>Enforce the ~100 ms minimum spacing between Scryfall requests.</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttleGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextAllowed)
                await Task.Delay(_nextAllowed - now, ct);
            _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
        }
        finally { _throttleGate.Release(); }
    }

    /// <summary>Parse a Scryfall price string ("34.13", invariant dot decimal) to a decimal, or null.</summary>
    private static decimal? ParsePrice(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    // --- Minimal Scryfall JSON DTOs (Web naming: snake_case handled per [JsonPropertyName]). ---

    private sealed class ScryfallCard
    {
        [JsonPropertyName("object")] public string? Object { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("set")] public string? Set { get; set; }
        [JsonPropertyName("set_name")] public string? SetName { get; set; }
        [JsonPropertyName("prices")] public ScryfallPrices? Prices { get; set; }
        [JsonPropertyName("purchase_uris")] public ScryfallPurchaseUris? PurchaseUris { get; set; }
    }

    private sealed class ScryfallPrices
    {
        [JsonPropertyName("eur")] public string? Eur { get; set; }
        [JsonPropertyName("eur_foil")] public string? EurFoil { get; set; }
    }

    private sealed class ScryfallPurchaseUris
    {
        [JsonPropertyName("cardmarket")] public string? Cardmarket { get; set; }
    }

    private sealed class ScryfallSearch
    {
        [JsonPropertyName("data")] public List<ScryfallCard>? Data { get; set; }
    }
}
