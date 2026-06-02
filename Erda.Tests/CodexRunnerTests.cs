using Erda.Services;
using Xunit;

namespace Erda.Tests;

public class CodexRunnerTests
{
    [Theory]
    [InlineData("low", "low")]
    [InlineData("High", "high")]    // case-insensitive, normalized to lower
    [InlineData("MEDIUM", "medium")]
    [InlineData("minimal", "minimal")]
    [InlineData(" high ", "high")]  // trimmed
    public void Accepts_known_efforts(string requested, string expected)
        => Assert.Equal(expected, CodexRunner.NormalizeReasoningEffort(requested, "high"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("turbo")]   // unknown value
    public void Falls_back_to_default_for_missing_or_unknown(string? requested)
        => Assert.Equal("high", CodexRunner.NormalizeReasoningEffort(requested, "high"));
}
