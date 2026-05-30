using Erda.Scheduling;
using Xunit;

namespace Erda.Tests;

public class SeqFilterTests
{
    [Fact]
    public void LevelsAtOrAbove_Error_is_Error_and_Fatal()
    {
        var levels = SeqFilter.LevelsAtOrAbove("Error");
        Assert.Equal(["Error", "Fatal"], levels);
    }

    [Fact]
    public void LevelsAtOrAbove_unknown_defaults_to_Error()
    {
        var levels = SeqFilter.LevelsAtOrAbove("Nonsense");
        Assert.Equal(["Error", "Fatal"], levels);
    }

    [Fact]
    public void ForMinLevel_builds_level_clause()
    {
        var f = SeqFilter.ForMinLevel("Error");
        Assert.Contains("@Level = 'Error'", f);
        Assert.Contains("@Level = 'Fatal'", f);
        Assert.Contains(" or ", f);
    }

    [Fact]
    public void ForMinLevel_ands_in_extra_filter()
    {
        var f = SeqFilter.ForMinLevel("Error", "Application = 'Erda'");
        Assert.Contains("Application = 'Erda'", f);
        Assert.Contains(") and (", f);
    }
}
