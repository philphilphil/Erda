using System.Globalization;
using System.Text;

namespace Erda.Agents.Tools;

/// <summary>
/// Builds Cardmarket <b>card-level</b> ("all printings") URLs filtered to German sellers + a language,
/// from a card name. Cardmarket's <c>/Cards/&lt;slug&gt;</c> page aggregates every printing of a card,
/// so the cheapest German/English copy across all sets surfaces — the right baseline for buying in
/// person (a specific set/printing rarely matters when you just want the floor price). This avoids the
/// per-printing product page entirely, so no <c>idProduct</c> redirect handling is needed.
///
/// The slug is derived from the card name: diacritics are folded to ASCII (û → u), spaces and hyphens
/// become '-', and other punctuation (commas, apostrophes, '.', '/') is dropped — matching Cardmarket's
/// own slugs (e.g. "Food Chain" → <c>Food-Chain</c>, "Ragavan, Nimble Pilferer" → <c>Ragavan-Nimble-Pilferer</c>).
/// VERIFY the slug rules + filter IDs against a live Cardmarket page.
/// </summary>
public static class CardmarketUrl
{
    /// <summary>Cardmarket's <c>sellerCountry</c> id for Germany.</summary>
    public const int GermanySellerCountry = 7;

    private const string CardsBase = "https://www.cardmarket.com/en/Magic/Cards/";

    /// <summary>
    /// The card-level listings URL filtered to German sellers + the given language id (1 = English,
    /// 3 = German). Used both as the scrape target and as the tappable fallback link handed to Phil.
    /// </summary>
    public static string CardPage(string cardName, int language) =>
        $"{CardsBase}{Slug(cardName)}?sellerCountry={GermanySellerCountry}&language={language}";

    /// <summary>Derive Cardmarket's card slug from a card name (see the type summary for the rules).</summary>
    internal static string Slug(string cardName)
    {
        if (string.IsNullOrWhiteSpace(cardName))
            return "";

        // Decompose so diacritics become base letter + combining mark, then drop the marks (û → u).
        var decomposed = cardName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue; // combining diacritic — drop

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSeparator && sb.Length > 0)
                    sb.Append('-');
                pendingSeparator = false;
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch == '-')
            {
                if (sb.Length > 0)
                    pendingSeparator = true; // space or existing hyphen → one '-' before the next letter
            }
            // else: punctuation (',', '\'', '.', '/', ':', …) → dropped, emits no separator
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
