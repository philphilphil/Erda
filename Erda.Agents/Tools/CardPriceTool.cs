using System.ComponentModel;
using System.Globalization;
using System.Text;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Agents.Tools;

/// <summary>
/// The <c>card_price</c> MAF tool: given a (possibly voice-garbled) Magic card name, resolves it on
/// Scryfall and returns the card's EUR trend price (Scryfall's <c>prices.eur</c> <b>is</b> the
/// Cardmarket trend), a tappable Cardmarket link filtered to <b>German sellers + English cards</b>
/// (all printings), and — when available — downloads the card image so the orchestrator can send it
/// over WhatsApp with <c>send_image</c>. Pure HTTP: no browser involved (Cardmarket's live listings
/// sit behind a Cloudflare JS challenge, so scraping them was dropped — Phil taps the link instead).
/// An ambiguous name returns a "did you mean" candidate list (no prices) for the orchestrator to
/// confirm with Phil first.
/// </summary>
public sealed class CardPriceTool(IScryfallClient scryfall, IOptions<WhatsAppOptions> whatsAppOptions)
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
        "Look up a Magic: The Gathering card's price — Phil's baseline when buying cards in person. " +
        "Give the card name (voice input may be garbled — pass your best guess); optionally the set " +
        "code to pin a specific printing. Returns the Cardmarket EUR trend price, a Cardmarket link " +
        "filtered to German sellers + English cards that Phil can tap to see live offers, and usually " +
        "a downloaded card image file path — send that image to Phil with send_image alongside the " +
        "price text. IMPORTANT: if the result is a 'did you mean' candidate list (not prices), the " +
        "name was ambiguous — ask Phil which card he means, then call card_price again with the " +
        "confirmed name (and set if he named one). Do not guess which candidate he meant.")]
    private async Task<string> CardPrice(
        [Description("The card name (your best guess if the voice input was garbled), e.g. 'Ragavan, Nimble Pilferer'.")] string name,
        [Description("Optional set code to pin a specific printing, e.g. 'mh2'. Omit to let Scryfall pick.")] string? set = null,
        [Description("Card language for the Cardmarket link: 'en' (English, default) or 'de' (German).")] string? language = "en")
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
            CardResolution.Match match => await FormatMatch(match, set, language),
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

    private async Task<string> FormatMatch(CardResolution.Match match, string? set, string? language)
    {
        var languageId = MapLanguage(language);
        // The card-level (all printings) Cardmarket page, filtered to German sellers + the language —
        // built from the card name, so it works even when Scryfall has no per-printing product link.
        var link = CardmarketUrl.CardPage(match.Name, languageId);

        var sb = new StringBuilder();
        sb.Append(match.Name).Append(" (").Append(SetLabel(match.SetCode));
        if (!string.IsNullOrWhiteSpace(match.SetName))
            sb.Append(" — ").Append(match.SetName);
        sb.Append(')');

        if (match.EurTrend is { } trend)
        {
            sb.Append("\nTrend: ").Append(FormatEur(trend));
            if (match.EurFoilTrend is { } foil)
                sb.Append(" (Foil: ").Append(FormatEur(foil)).Append(')');
        }
        else if (match.EurFoilTrend is { } foilOnly)
        {
            sb.Append("\nTrend (Foil): ").Append(FormatEur(foilOnly));
        }
        else
        {
            sb.Append("\nNo EUR trend price on Scryfall for this printing.");
        }

        sb.Append("\nDE sellers (").Append(LanguageLabel(languageId)).Append("): ").Append(link);

        if (string.IsNullOrWhiteSpace(set))
            sb.Append("\n(Trend is for the ").Append(SetLabel(match.SetCode))
                .Append(" printing; say the set to pick another.)");

        var imagePath = await TryDownloadImageAsync(match);
        if (imagePath is not null)
            sb.Append("\nCard image saved to ").Append(imagePath).Append(" — send it to Phil with send_image.");

        return sb.ToString();
    }

    /// <summary>Download the card image into the WhatsApp media directory (the volume <c>send_image</c>
    /// reads from). Returns the absolute path, or null when there is no image / no media dir / the
    /// download fails — the tool then simply returns text without an image.</summary>
    private async Task<string?> TryDownloadImageAsync(CardResolution.Match match)
    {
        var mediaDir = whatsAppOptions.Value.MediaTempDir;
        if (string.IsNullOrWhiteSpace(match.ImageUrl) || string.IsNullOrWhiteSpace(mediaDir))
            return null;

        var path = Path.Combine(mediaDir, $"card-{CardmarketUrl.Slug(match.Name).ToLowerInvariant()}.jpg");
        return await scryfall.TryDownloadImageAsync(match.ImageUrl, path) ? path : null;
    }

    private static string SetLabel(string setCode) =>
        string.IsNullOrWhiteSpace(setCode) ? "?" : setCode.ToUpperInvariant();

    /// <summary>Format a EUR amount German-style with a leading € — e.g. 31.00m → "€31,00".</summary>
    private static string FormatEur(decimal amount) => "€" + amount.ToString("0.00", De);
}
