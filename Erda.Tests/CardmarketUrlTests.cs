using Erda.Agents.Tools;
using Xunit;

namespace Erda.Tests;

public class CardmarketUrlTests
{
    [Theory]
    [InlineData("Food Chain", "Food-Chain")]                             // simple space
    [InlineData("Vivi Ornitier", "Vivi-Ornitier")]                       // simple space (user-verified)
    [InlineData("Ragavan, Nimble Pilferer", "Ragavan-Nimble-Pilferer")]  // comma dropped, spaces → '-'
    [InlineData("Gaea's Cradle", "Gaeas-Cradle")]                        // apostrophe dropped (no separator)
    [InlineData("Lim-Dûl's Vault", "Lim-Duls-Vault")]                    // diacritic folded, hyphen kept, apostrophe dropped
    [InlineData("Fire // Ice", "Fire-Ice")]                              // split card: '//' + spaces collapse to one '-'
    [InlineData("  Sol   Ring  ", "Sol-Ring")]                           // trim + collapse runs of whitespace
    public void Slug_matches_cardmarket_conventions(string name, string expected) =>
        Assert.Equal(expected, CardmarketUrl.Slug(name));

    [Fact]
    public void CardPage_builds_the_filtered_all_printings_url()
    {
        Assert.Equal(
            "https://www.cardmarket.com/en/Magic/Cards/Food-Chain?sellerCountry=7&language=1",
            CardmarketUrl.CardPage("Food Chain", 1));
        Assert.Equal(
            "https://www.cardmarket.com/en/Magic/Cards/Food-Chain?sellerCountry=7&language=3",
            CardmarketUrl.CardPage("Food Chain", 3));
    }
}
