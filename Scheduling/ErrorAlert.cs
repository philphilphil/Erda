using System.Text;
using Erda.Services.Seq;

namespace Erda.Scheduling;

/// <summary>Formats a Seq error (plus optional Codex analysis) into a WhatsApp-friendly message.</summary>
public static class ErrorAlert
{
    private const int MaxLength = 3500; // WhatsApp hard limit is ~4096; leave headroom.

    public static string Format(SeqError error, string? analysis, int occurrences = 1)
    {
        var sb = new StringBuilder();
        sb.Append("🚨 ").Append(error.Level);
        var source = Source(error);
        if (!string.IsNullOrWhiteSpace(source))
            sb.Append(" · ").Append(source);
        if (occurrences > 1)
            sb.Append(" (×").Append(occurrences).Append(')');
        sb.Append('\n');

        sb.Append(Truncate(error.Display.Trim(), 500));
        if (!string.IsNullOrWhiteSpace(error.ExceptionType))
            sb.Append('\n').Append(error.ExceptionType);
        sb.Append("\n🕒 ").Append(error.Timestamp.ToString("u"));

        if (!string.IsNullOrWhiteSpace(analysis))
            sb.Append("\n\n— Codex —\n").Append(analysis.Trim());

        return Truncate(sb.ToString(), MaxLength);
    }

    private static string Source(SeqError e)
    {
        if (e.Properties.TryGetValue("Application", out var app) && !string.IsNullOrWhiteSpace(app))
            return app;
        if (e.Properties.TryGetValue("SourceContext", out var sc) && !string.IsNullOrWhiteSpace(sc))
            return sc;
        return "";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
