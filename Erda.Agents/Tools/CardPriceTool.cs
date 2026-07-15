using System.ComponentModel;
using System.Globalization;
using System.Text;
using Erda.Core.Services;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// The <c>card_price</c> MAF tool: given a (possibly voice-garbled) Magic card name, resolves it on
/// Scryfall, then drives the shared browser to read the cheapest <b>German-seller</b> offers on
/// Cardmarket for the <b>English</b> printing — Phil's in-person negotiating baseline. It degrades
/// gracefully: an ambiguous name returns a "did you mean" candidate list (no prices) for the
/// orchestrator to confirm; a blocked/changed Cardmarket page falls back to the Scryfall EUR trend plus
/// a tappable Germany/English-filtered link — never nothing.
/// </summary>
public sealed class CardPriceTool(IScryfallClient scryfall, ICardmarketPriceService cardmarket)
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Wrap <see cref="CardPrice"/> as the single <c>card_price</c> tool (like NotifyTools/ReminderTools).</summary>
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(CardPrice, "card_price"),
    ];

    /// <summary>Map the language code to Cardmarket's language id: en → 1, de → 3. Extendable; anything
    /// else defaults to English (1).</summary>
    internal static int MapLanguage(string? language) => (language ?? "en").Trim().ToLowerInvariant() switch
    {
        "de" or "german" or "deutsch" => 3,
        _ => 1, // English (default) and anything unmapped
    };

    private static string LanguageLabel(int id) => id == 3 ? "German" : "English";

    [Description(
        "Look up what a German seller charges on Cardmarket for a Magic: The Gathering single — Phil's " +
        "negotiating baseline when buying cards in person. Give the card name (voice input may be " +
        "garbled — pass your best guess); optionally the set code to pin a specific printing. Returns " +
        "the cheapest German-seller offers for the English printing plus the EUR trend price. " +
        "IMPORTANT: if the result is a 'did you mean' candidate list (not prices), the name was " +
        "ambiguous — ask Phil which card he means, then call card_price again with the confirmed name " +
        "(and set if he named one). Do not guess which candidate he meant.")]
    private async Task<string> CardPrice(
        [Description("The card name (your best guess if the voice input was garbled), e.g. 'Ragavan, Nimble Pilferer'.")] string name,
        [Description("Optional set code to pin a specific printing, e.g. 'mh2'. Omit to let Scryfall pick.")] string? set = null,
        [Description("How many of the cheapest offers to return (default 5).")] int count = 5,
        [Description("Card/offer language: 'en' (English, default) or 'de' (German).")] string? language = "en")
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Tell me which card to price.";

        CardResolution resolution;
        try
        {
            resolution = await scryfall.ResolveAsync(name, set);
        }
        catch (Exception)
        {
            return "Couldn't reach Scryfall to look up that card — try again in a moment.";
        }

        return resolution switch
        {
            CardResolution.NotFound => $"Couldn't find a card named \"{name.Trim()}\" on Scryfall.",
            CardResolution.Candidates candidates => FormatCandidates(name.Trim(), candidates),
            CardResolution.Match match => await FormatMatch(match, set, count, language),
            _ => $"Couldn't find a card named \"{name.Trim()}\".",
        };
    }

    private static string FormatCandidates(string query, CardResolution.Candidates candidates)
    {
        var sb = new StringBuilder();
        sb.Append("A few cards could match \"").Append(query).Append("\" — which one?");
        foreach (var candidate in candidates.Names)
            sb.Append("\n• ").Append(candidate);
        return sb.ToString();
    }

    private async Task<string> FormatMatch(CardResolution.Match match, string? set, int count, string? language)
    {
        var languageId = MapLanguage(language);
        var header = $"{match.Name} ({SetLabel(match.SetCode)}) — {LanguageLabel(languageId)}, DE sellers";

        // Price against Cardmarket's card-level page (all printings), filtered to German sellers + the
        // language — built from the card name, so the same URL is both the scrape target and the tappable
        // fallback link. The set only pins which printing's trend Scryfall returned, not the scrape.
        var filteredLink = CardmarketUrl.CardPage(match.Name, languageId);
        var offers = await cardmarket.GetGermanOffersAsync(filteredLink, Math.Max(1, count));

        var result = new StringBuilder(header);
        if (offers.Count > 0)
        {
            var i = 1;
            foreach (var offer in offers)
            {
                result.Append("\n").Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append(". ")
                    .Append(FormatEur(offer.Price));
                if (!string.IsNullOrWhiteSpace(offer.Condition))
                    result.Append(" · ").Append(offer.Condition);
                if (!string.IsNullOrWhiteSpace(offer.Seller))
                    result.Append(" · ").Append(offer.Seller);
                i++;
            }
            result.Append("\nTrend: ").Append(match.EurTrend is { } t ? FormatEur(t) : "n/a")
                .Append(" · ").Append(filteredLink);
            return result.ToString();
        }

        // Fallback: Cardmarket blocked/empty/changed — hand back the trend + a tappable filtered link.
        result.Append("\nCouldn't read live Cardmarket offers right now.");
        if (match.EurTrend is { } trend2)
            result.Append("\nTrend: ").Append(FormatEur(trend2)).Append(" · ").Append(filteredLink);
        else
            result.Append("\n").Append(filteredLink);
        if (string.IsNullOrWhiteSpace(set))
            result.Append("\n(assumed ").Append(SetLabel(match.SetCode)).Append("; say the set to pick another.)");
        return result.ToString();
    }

    private static string SetLabel(string setCode) =>
        string.IsNullOrWhiteSpace(setCode) ? "?" : setCode.ToUpperInvariant();

    /// <summary>Format a EUR amount German-style with a leading € — e.g. 31.00m → "€31,00".</summary>
    private static string FormatEur(decimal amount) => "€" + amount.ToString("0.00", De);
}
