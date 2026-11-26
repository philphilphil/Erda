using System.Text;
using Erda.Core.Services;
using Erda.Core.Services.Seq;

namespace Erda.Core.Scheduling;

/// <summary>Produces a short, practical analysis of an error.</summary>
public interface IErrorAnalyzer
{
    Task<string> AnalyzeAsync(SeqError error, CancellationToken cancellationToken = default);
}

/// <summary>
/// Analyzes a Seq error with the in-process reasoner (the configured chat model). Web search is off:
/// this is reasoning over the error text alone. Failures degrade to a short note rather
/// than throwing, so one bad analysis never stops the scheduler.
/// </summary>
public sealed class ReasonerErrorAnalyzer(IReasoner reasoner, ILogger<ReasonerErrorAnalyzer> logger) : IErrorAnalyzer
{
    public async Task<string> AnalyzeAsync(SeqError error, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await reasoner.ReasonAsync(BuildPrompt(error), webSearch: false, cancellationToken, logLabel: "error analysis")).Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error analysis failed for error {Id}.", error.Id);
            return $"(Analysis unavailable: {ex.Message})";
        }
    }

    private static string BuildPrompt(SeqError e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are a senior engineer triaging a production error from the Seq log server. " +
            "Give a brief, practical analysis: the most likely root cause and one concrete suggested " +
            "fix or next diagnostic step. Keep it under ~150 words, plain text suitable for a WhatsApp " +
            "message (no markdown headings). Do not restate the whole error.");
        sb.AppendLine();
        sb.AppendLine($"Level: {e.Level}");
        sb.AppendLine($"Time: {e.Timestamp:u}");
        sb.AppendLine($"Message: {e.Display}");
        if (!string.IsNullOrWhiteSpace(e.Exception))
        {
            sb.AppendLine("Exception:");
            sb.AppendLine(Truncate(e.Exception!, 2000));
        }
        if (e.Properties.Count > 0)
        {
            sb.AppendLine("Properties:");
            foreach (var kv in e.Properties.Take(15))
                sb.AppendLine($"  {kv.Key} = {kv.Value}");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
