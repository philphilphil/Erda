using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI.Workflows;

namespace Erda.Agents.Workflows.Executors;

/// <summary>
/// Recipe importer, step 2: page HTML → readable text for the model. Keeps any embedded
/// <c>schema.org/Recipe</c> JSON-LD (the most reliable source), drops scripts/styles/tags, decodes
/// entities, and caps the length to protect the token budget.
/// </summary>
internal sealed partial class ExtractRecipeTextExecutor() : Executor<string, string>("extract")
{
    private const int MaxChars = 16_000;

    public override ValueTask<string> HandleAsync(
        string html, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(Clean(html));

    /// <summary>HTML → readable text (JSON-LD recipe data first, then the stripped page text), capped.</summary>
    internal static string Clean(string html)
    {
        var jsonLd = new StringBuilder();
        foreach (Match m in JsonLdRegex().Matches(html))
        {
            var content = m.Groups[1].Value.Trim();
            if (content.Contains("Recipe", StringComparison.OrdinalIgnoreCase))
                jsonLd.AppendLine(content);
        }

        var text = ScriptStyleRegex().Replace(html, " ");
        text = BreakRegex().Replace(text, "\n");   // block-enders → newlines, so steps stay separate
        text = TagRegex().Replace(text, "");        // strip remaining tags
        text = WebUtility.HtmlDecode(text).Replace(' ', ' '); // nbsp → normal space
        text = SpacesRegex().Replace(text, " ");
        text = BlankLinesRegex().Replace(text, "\n\n").Trim();

        var combined = jsonLd.Length > 0
            ? $"[structured recipe data]\n{jsonLd}\n\n[page text]\n{text}"
            : text;
        return combined.Length > MaxChars ? combined[..MaxChars] : combined;
    }

    [GeneratedRegex("""<script[^>]*type=["']application/ld\+json["'][^>]*>(.*?)</script>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdRegex();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<\s*(br|/p|/li|/h[1-6]|/div|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
