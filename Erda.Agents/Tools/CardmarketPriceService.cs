using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Erda.Agents.Tools;

/// <summary>A single German-seller offer scraped from a Cardmarket product page.</summary>
public sealed record CardmarketOffer(decimal Price, string Condition, string Seller);

/// <summary>Reads the lowest German-seller offers for a card off its Cardmarket card-level page.</summary>
public interface ICardmarketPriceService
{
    /// <summary>
    /// Drive the shared browser to an already-filtered Cardmarket card-level URL (see
    /// <see cref="CardmarketUrl.CardPage"/>) — German sellers + a language, all printings — scrape the
    /// cheapest offers, and return up to <paramref name="count"/> of them. Never throws: any failure
    /// (browser down, Cloudflare challenge, changed DOM, timeout) yields an empty list so the tool can
    /// fall back to the trend price + a tappable link.
    /// </summary>
    Task<IReadOnlyList<CardmarketOffer>> GetGermanOffersAsync(
        string cardPageUrl, int count, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ICardmarketPriceService"/> over the existing Playwright MCP browser (<see cref="IBrowserMcp"/>).
/// It invokes the MCP tools (<c>browser_navigate</c>, <c>browser_evaluate</c>) <b>directly as
/// <see cref="AIFunction"/>s</b> — no orchestrator/LLM loop — for a fast, deterministic one-navigation +
/// one-evaluate fetch. Cardmarket blocks plain HTTP behind Cloudflare, so a real (cookie-warmed) browser
/// is required; the persistent user-data-dir carries the clearance cookie across fetches.
///
/// A private <see cref="SemaphoreSlim"/> serializes navigations because the browser tab is shared with
/// the <c>browse_web</c> sub-agent.
/// </summary>
public sealed class CardmarketPriceService(IBrowserMcp browser, ILogger<CardmarketPriceService> logger)
    : ICardmarketPriceService
{
    // The offer-row selectors below are the one fragile spot: they cannot be confirmed from a script
    // (CM blocks curl); the warmed browser can. The filter ids live in CardmarketUrl.
    // VERIFY against a live Cardmarket page — selectors assumed.

    /// <summary>
    /// The single, centralized page-scraping snippet (a zero-arg JS function, as a string, for
    /// <c>browser_evaluate</c>). It reads the offer rows into a JSON array of <c>{price, condition,
    /// seller}</c> — <c>price</c> kept as the raw German-formatted text (e.g. "31,00 €"), parsed to a
    /// decimal in C#. Selectors are deliberately tolerant (several fallbacks each) since the exact
    /// Cardmarket markup can only be confirmed against a live page.
    /// VERIFY against a live Cardmarket page — selectors + filter IDs assumed.
    /// </summary>
    private const string ScrapeOffersJs = """
        () => {
          const rows = Array.from(document.querySelectorAll(
            '.article-row, [id^="articleRow"], .table-body [class*="row"]'));
          const out = [];
          for (const row of rows) {
            const priceEl = row.querySelector(
              '.price-container, .col-offer .price, .st_price, [class*="price"]');
            const price = priceEl ? priceEl.textContent.trim() : '';
            if (!price) continue;
            const condEl = row.querySelector(
              '.article-condition span, .badge, [class*="condition"], [class*="Condition"]');
            const condition = condEl
              ? (condEl.getAttribute('title') || condEl.textContent || '').trim()
              : '';
            const sellerEl = row.querySelector(
              '.seller-name a, .seller-info a, [href*="/Users/"], [class*="seller"] a');
            const seller = sellerEl ? sellerEl.textContent.trim() : '';
            out.push({ price, condition, seller });
          }
          return out;
        }
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<CardmarketOffer>> GetGermanOffersAsync(
        string cardPageUrl, int count, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cardPageUrl) || !browser.Enabled || browser.Tools.Count == 0)
            return [];

        await _gate.WaitAsync(ct);
        try
        {
            var navigate = FindTool("browser_navigate");
            var evaluate = FindTool("browser_evaluate");
            if (navigate is null || evaluate is null)
            {
                logger.LogWarning("Cardmarket scrape skipped: browser_navigate/browser_evaluate not available.");
                return [];
            }

            // The URL is the already-filtered card-level page (all printings, German sellers, language),
            // built by CardmarketUrl.CardPage — no redirect handling needed. Navigate, let the offer
            // table settle, then scrape.
            await navigate.InvokeAsync(new AIFunctionArguments { ["url"] = cardPageUrl }, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);

            var evalResult = await evaluate.InvokeAsync(new AIFunctionArguments { ["function"] = ScrapeOffersJs }, ct);
            var json = ExtractOffersJson(ResultText(evalResult));
            var offers = ParseOffers(json, count);
            logger.LogInformation("Cardmarket scrape: {Count} offers from {Url}.", offers.Count, cardPageUrl);
            return offers;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cardmarket scrape failed for {Url}; falling back to trend.", cardPageUrl);
            return [];
        }
        finally { _gate.Release(); }
    }

    private AIFunction? FindTool(string name) =>
        browser.Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)) as AIFunction;

    /// <summary>Parse the <c>browser_evaluate</c> JSON (<c>[{price, condition, seller}]</c>) into offers,
    /// mapping the German price format "31,00 €" → 31.00m, and cap at <paramref name="count"/>. Rows with
    /// an unparseable price are skipped; a missing condition/seller defaults to empty. Never throws.</summary>
    internal static IReadOnlyList<CardmarketOffer> ParseOffers(string json, int count)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(json))
            return [];

        List<OfferDto>? rows;
        try { rows = JsonSerializer.Deserialize<List<OfferDto>>(json); }
        catch (JsonException) { return []; }
        if (rows is null || rows.Count == 0)
            return [];

        var offers = new List<CardmarketOffer>();
        foreach (var row in rows)
        {
            if (!TryParseGermanPrice(row.Price, out var price))
                continue;
            offers.Add(new CardmarketOffer(price, (row.Condition ?? "").Trim(), (row.Seller ?? "").Trim()));
            if (offers.Count >= count)
                break;
        }
        return offers;
    }

    /// <summary>Parse a German-formatted Cardmarket price ("31,00 €", "1.234,56 €") to a decimal.</summary>
    private static bool TryParseGermanPrice(string? raw, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        // Keep only digits and the German thousands '.' / decimal ',' — drops the currency symbol,
        // spaces (incl. the non-breaking space CM uses), and any stray characters.
        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out price);
    }

    /// <summary>Pull the offers JSON array out of a <c>browser_evaluate</c> response for
    /// <see cref="ScrapeOffersJs"/>. The Playwright MCP formats the response as several <c>### </c>-delimited
    /// markdown sections (Result, Ran Playwright code, Page, Snapshot, …); the evaluate return value lives only
    /// in <c>### Result</c>. We must slice that section out FIRST — the echoed JS source in
    /// <c>### Ran Playwright code</c> is full of <c>[</c>/<c>]</c> (e.g. <c>const out = []</c>,
    /// <c>[id^="articleRow"]</c>) that would otherwise be swept into the array span.</summary>
    internal static string ExtractOffersJson(string mcpText) => ExtractJsonArray(ResultSection(mcpText));

    /// <summary>Isolate the content of the MCP response's <c>### Result</c> section. Sections are delimited by
    /// <c>### </c> headers at line start (see the Playwright MCP response format); the Result section holds the
    /// evaluate return value. Returns "" when absent.</summary>
    private static string ResultSection(string mcpText)
    {
        if (string.IsNullOrEmpty(mcpText))
            return "";
        const string header = "### Result";
        var i = mcpText.IndexOf(header, StringComparison.Ordinal);
        if (i < 0)
            return "";
        var start = i + header.Length;
        // Content runs to the next "### " section header (headers sit at line start) or end of text.
        var next = mcpText.IndexOf("\n### ", start, StringComparison.Ordinal);
        var content = next < 0 ? mcpText[start..] : mcpText[start..next];
        return content.Trim();
    }

    /// <summary>Slice out the outermost JSON array from a string (first <c>[</c> to last <c>]</c>). Callers
    /// must pass an already-isolated section — never the whole MCP blob — so stray brackets from other
    /// sections cannot corrupt the span.</summary>
    private static string ExtractJsonArray(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : "";
    }

    /// <summary>Collapse an MCP tool result to its text. Reads the <see cref="CallToolResult"/> text
    /// blocks when possible, else falls back to the object's string form.</summary>
    private static string ResultText(object? result)
    {
        if (result is CallToolResult call)
            return string.Concat(call.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return result?.ToString() ?? "";
    }

    private sealed class OfferDto
    {
        [JsonPropertyName("price")] public string? Price { get; set; }
        [JsonPropertyName("condition")] public string? Condition { get; set; }
        [JsonPropertyName("seller")] public string? Seller { get; set; }
    }
}
