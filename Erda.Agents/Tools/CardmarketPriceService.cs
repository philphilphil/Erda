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
    // (CM blocks curl); a real browser can. Verified live against a card-level page on 2026-07-15 —
    // recheck here if scrapes start coming back empty. The filter ids live in CardmarketUrl.

    /// <summary>
    /// The single, centralized page-scraping snippet (a zero-arg JS function, as a string, for
    /// <c>browser_evaluate</c>). It reads the offer rows into an array of <c>{price, condition, seller}</c>
    /// — <c>price</c> kept as the raw German-formatted text (e.g. "31,00 €"), parsed to a decimal in C#.
    /// Selectors verified against a live Cardmarket <c>/Cards/&lt;slug&gt;</c> page (2026-07-15):
    /// rows are <c>.article-row</c>; price is <c>.price-container</c>; condition is the badge inside
    /// <c>.article-condition</c> ("NM"/"EX"/…), with the full name in <c>data-bs-original-title</c>; the
    /// seller is the <c>/Users/</c> link under <c>.seller-name</c>. IMPORTANT: the condition must be read
    /// from <c>.article-condition</c> specifically — a bare <c>.badge</c> matches the seller's sales-count
    /// badge, which sits earlier in the row. Tolerant fallbacks are kept for minor markup drift.
    /// </summary>
    private const string ScrapeOffersJs = """
        () => {
          const rows = Array.from(document.querySelectorAll('.article-row, [id^="articleRow"]'));
          const out = [];
          for (const row of rows) {
            const priceEl = row.querySelector('.price-container, [class*="price-container"], [class*="price"]');
            const price = priceEl ? priceEl.textContent.trim() : '';
            if (!price) continue;
            const condEl = row.querySelector('.article-condition');
            const condition = condEl
              ? (condEl.querySelector('.badge')?.textContent.trim()
                 || condEl.getAttribute('data-bs-original-title') || '').trim()
              : '';
            const sellerEl = row.querySelector('.seller-name a, [href*="/Users/"]');
            const seller = sellerEl ? sellerEl.textContent.trim() : '';
            out.push({ price, condition, seller });
          }
          return out;
        }
        """;

    // Poll for the offer table for up to MaxProbeAttempts × ProbeInterval before giving up. This both
    // tolerates a slow load and gives any Cloudflare interstitial time to resolve (it won't, headless —
    // but the probe then tells us so).
    private const int MaxProbeAttempts = 8;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(1000);

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
            // built by CardmarketUrl.CardPage — no redirect handling needed. Navigate, then poll for the
            // offer table to appear (rather than one blind wait), so slow loads succeed and a bot
            // challenge is diagnosable.
            await navigate.InvokeAsync(new AIFunctionArguments { ["url"] = cardPageUrl }, ct);

            PageProbe probe = default;
            for (var attempt = 0; attempt < MaxProbeAttempts; attempt++)
            {
                await Task.Delay(ProbeInterval, ct);
                probe = await ProbePageAsync(evaluate, ct);
                if (probe.Rows > 0)
                    break;
            }

            // No rows after the poll window: report WHY, so the fallback isn't a silent mystery.
            // A Cloudflare/bot challenge (headless browsers get flagged) is the prime suspect; distinguish
            // it from a genuinely empty listing / changed markup by the page title.
            if (probe.Rows == 0)
            {
                if (probe.Challenge)
                    logger.LogWarning(
                        "Cardmarket scrape blocked by a bot/Cloudflare challenge (page title {Title}) for {Url}. " +
                        "A headless browser is being challenged — a headful/warmed browser profile is needed.",
                        probe.Title, cardPageUrl);
                else
                    logger.LogWarning(
                        "Cardmarket scrape found no offer rows (page title {Title}, challenge=false) for {Url} — " +
                        "empty listing or changed markup.", probe.Title, cardPageUrl);
                return [];
            }

            var evalResult = await evaluate.InvokeAsync(new AIFunctionArguments { ["function"] = ScrapeOffersJs }, ct);
            var json = ExtractOffersJson(ResultText(evalResult));
            var offers = ParseOffers(json, count);
            logger.LogInformation("Cardmarket scrape: {Count} offers (of {Rows} rows) from {Url}.",
                offers.Count, probe.Rows, cardPageUrl);
            return offers;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cardmarket scrape failed for {Url}; falling back to trend.", cardPageUrl);
            return [];
        }
        finally { _gate.Release(); }
    }

    /// <summary>A cheap page-state probe used while polling: how many offer rows are present, the page
    /// title, and whether the page looks like a bot/Cloudflare challenge.</summary>
    internal readonly record struct PageProbe(int Rows, string Title, bool Challenge);

    /// <summary>Read the offer-row count + title + challenge flag from the live page.</summary>
    private async Task<PageProbe> ProbePageAsync(AIFunction evaluate, CancellationToken ct)
    {
        var result = await evaluate.InvokeAsync(new AIFunctionArguments { ["function"] = ProbePageJs }, ct);
        return ParseProbe(ResultText(result));
    }

    /// <summary>The probe JS: offer-row count, page title, and a challenge heuristic (title/body text of a
    /// Cloudflare interstitial). Returns an object, so the MCP's <c>### Result</c> holds clean JSON.</summary>
    private const string ProbePageJs = """
        () => {
          const rows = document.querySelectorAll('.article-row, [id^="articleRow"]').length;
          const hay = (document.title + ' ' + (document.body ? document.body.innerText.slice(0, 300) : '')).toLowerCase();
          const challenge = /just a moment|attention required|verify you are human|checking your browser|cf-browser-verification|cloudflare/.test(hay);
          return { rows, title: document.title, challenge };
        }
        """;

    /// <summary>Parse the probe's <c>### Result</c> JSON object into a <see cref="PageProbe"/>. Returns an
    /// empty probe (0 rows, no challenge) on any malformed/absent result.</summary>
    internal static PageProbe ParseProbe(string mcpText)
    {
        var json = ExtractResultObject(mcpText);
        if (json.Length == 0)
            return new PageProbe(0, "", false);
        try
        {
            var dto = JsonSerializer.Deserialize<ProbeDto>(json);
            return new PageProbe(dto?.Rows ?? 0, dto?.Title ?? "", dto?.Challenge ?? false);
        }
        catch (JsonException) { return new PageProbe(0, "", false); }
    }

    private AIFunction? FindTool(string name) =>
        browser.Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)) as AIFunction;

    /// <summary>Parse the <c>browser_evaluate</c> JSON (<c>[{price, condition, seller}]</c>) into offers,
    /// mapping the German price format "31,00 €" → 31.00m, and return the cheapest offer per <b>distinct
    /// seller</b> (CM lists rows cheapest-first, so the first occurrence of a seller is their lowest),
    /// capped at <paramref name="count"/>. Rows with an unparseable price are skipped; a missing
    /// condition/seller defaults to empty (blank sellers are not deduplicated). Never throws.</summary>
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
        var seenSellers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!TryParseGermanPrice(row.Price, out var price))
                continue;
            var seller = (row.Seller ?? "").Trim();
            // One offer per seller (their cheapest) — a single seller often lists several cheap copies,
            // and Phil wants the baseline across distinct sellers. Blank sellers can't be deduped.
            if (seller.Length > 0 && !seenSellers.Add(seller))
                continue;
            offers.Add(new CardmarketOffer(price, (row.Condition ?? "").Trim(), seller));
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

    /// <summary>Slice the outermost JSON object (first <c>{</c> to last <c>}</c>) out of the MCP response's
    /// <c>### Result</c> section — used for the page probe's <c>{rows, title, challenge}</c> return.</summary>
    private static string ExtractResultObject(string mcpText)
    {
        var section = ResultSection(mcpText);
        var start = section.IndexOf('{');
        var end = section.LastIndexOf('}');
        return start >= 0 && end > start ? section[start..(end + 1)] : "";
    }

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

    private sealed class ProbeDto
    {
        [JsonPropertyName("rows")] public int Rows { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("challenge")] public bool Challenge { get; set; }
    }
}
