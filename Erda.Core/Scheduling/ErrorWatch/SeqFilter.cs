namespace Erda.Scheduling;

/// <summary>
/// Builds Seq strict-filter expressions. The level filter expands a minimum level to the set of
/// levels at or above it (e.g. "Error" → Error and Fatal), since Seq levels are strings, not ranks.
/// </summary>
public static class SeqFilter
{
    // Seq severity order, low → high.
    private static readonly string[] Order =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    /// <summary>The set of level names at or above <paramref name="minLevel"/> (inclusive).</summary>
    public static IReadOnlyList<string> LevelsAtOrAbove(string minLevel)
    {
        var idx = Array.FindIndex(Order, l => l.Equals(minLevel, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            idx = Array.FindIndex(Order, l => l.Equals("Error", StringComparison.OrdinalIgnoreCase));
        return Order[idx..];
    }

    /// <summary>
    /// A strict filter matching events at or above <paramref name="minLevel"/>, optionally AND-ed
    /// with an extra user filter.
    /// </summary>
    public static string ForMinLevel(string minLevel, string? extra = null)
    {
        var levels = LevelsAtOrAbove(minLevel);
        var levelClause = "(" + string.Join(" or ", levels.Select(l => $"@Level = '{l}'")) + ")";
        return string.IsNullOrWhiteSpace(extra) ? levelClause : $"({levelClause}) and ({extra})";
    }
}
