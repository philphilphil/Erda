using Erda.Services.Seq;

namespace Erda.Scheduling;

/// <summary>
/// Computes a stable signature for an error so recurrences are recognized and not re-alerted every
/// poll. Signature = level + message template + exception type. Uses the template (not the rendered
/// message) so the same error with different parameter values collapses to one signature.
/// </summary>
public static class ErrorSignature
{
    public static string Compute(SeqError error)
    {
        var template = string.IsNullOrWhiteSpace(error.MessageTemplate)
            ? error.RenderedMessage
            : error.MessageTemplate;
        return $"{error.Level}|{template.Trim()}|{error.ExceptionType ?? ""}";
    }
}
