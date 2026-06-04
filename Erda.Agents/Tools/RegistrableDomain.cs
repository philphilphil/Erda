namespace Erda.Agents.Tools;

/// <summary>
/// Pure helper to reduce a host or URL to its <b>registrable domain</b> (eTLD+1), so the
/// <c>find_login</c> lookup matches a 1Password item even when the live page is on a subdomain or an
/// SSO redirect (e.g. <c>login.moxfield.com</c> → <c>moxfield.com</c>).
///
/// This is a pragmatic approximation, not a full Public Suffix List: it takes the last two labels,
/// or the last three when the last two form a known two-level public suffix (<c>co.uk</c> etc.). For
/// a single-user vault with one item per site this is sufficient; a full PSL is a later hardening.
/// </summary>
public static class RegistrableDomain
{
    // Common two-level public suffixes where the registrable domain needs three labels.
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk",
        "com.au", "net.au", "org.au",
        "co.jp", "co.nz", "co.za", "com.br", "co.in", "co.kr",
    };

    /// <summary>The registrable domain of a host or URL, lowercased; "" if none can be derived.</summary>
    public static string Of(string hostOrUrl)
    {
        var host = ExtractHost(hostOrUrl);
        if (host.Length == 0) return "";

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return "";   // bare "localhost" etc. — not registrable

        var lastTwo = string.Join('.', labels[^2..]);
        if (labels.Length >= 3 && TwoLevelSuffixes.Contains(lastTwo))
            return string.Join('.', labels[^3..]);
        return lastTwo;
    }

    /// <summary>True if <paramref name="hostOrUrl"/> shares a registrable domain with <paramref name="other"/>.</summary>
    public static bool Matches(string hostOrUrl, string other)
    {
        var a = Of(hostOrUrl);
        return a.Length > 0 && a.Equals(Of(other), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractHost(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = input.Trim();

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.ToLowerInvariant();

        // Not an absolute URL — treat as a bare host. Reject anything with spaces or no dot.
        if (s.Contains(' ')) return "";
        s = s.ToLowerInvariant();
        return s.Contains('.') ? s : "";
    }
}
