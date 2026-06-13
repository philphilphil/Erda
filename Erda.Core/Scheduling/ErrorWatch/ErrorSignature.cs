using Erda.Core.Services.Seq;

namespace Erda.Core.Scheduling;

/// <summary>
/// Computes a stable signature for an error so recurrences are recognized and not re-alerted every
/// poll. Signature = level + message template + exception type. Uses the template (not the rendered
/// message) so the same error with different parameter values collapses to one signature.
/// <para>
/// Some sources (e.g. Leporello's <c>scrape_error</c>) log a constant template and put all the
/// variability in structured properties, which would otherwise collapse every distinct failure into
/// one signature. <paramref name="signatureProperties"/> names properties to fold into the signature
/// so those events split per property value (e.g. per venue + error reason).
/// </para>
/// </summary>
public static class ErrorSignature
{
    public static string Compute(SeqError error, IReadOnlyList<string>? signatureProperties = null)
    {
        var template = string.IsNullOrWhiteSpace(error.MessageTemplate)
            ? error.RenderedMessage
            : error.MessageTemplate;
        var signature = $"{error.Level}|{template.Trim()}|{error.ExceptionType ?? ""}";

        if (signatureProperties is { Count: > 0 })
        {
            foreach (var name in signatureProperties)
            {
                error.Properties.TryGetValue(name, out var value);
                signature += $"|{name}={value ?? ""}";
            }
        }

        return signature;
    }
}
