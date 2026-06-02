namespace Erda.Core.WhatsApp;

/// <summary>
/// Pure helpers for WhatsApp JIDs (Jabber-style IDs). A user JID looks like
/// "4915123456789@s.whatsapp.net"; a linked-device JID may carry a ":NN" device suffix on the
/// user part ("4915123456789:12@s.whatsapp.net"). The whitelist compares on the bare user digits
/// so device suffixes and the domain don't matter.
/// </summary>
public static class WhatsAppJid
{
    private const string UserServer = "s.whatsapp.net";

    /// <summary>"+49 151 2345 6789" (or an existing JID) -&gt; "4915123456789@s.whatsapp.net".</summary>
    public static string FromNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return "";
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "" : $"{digits}@{UserServer}";
    }

    /// <summary>The bare numeric user part of a JID, stripped of any device suffix and domain.</summary>
    public static string BareUser(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid))
            return "";
        var user = jid;
        var at = user.IndexOf('@');
        if (at >= 0)
            user = user[..at];
        var colon = user.IndexOf(':'); // device/agent suffix
        if (colon >= 0)
            user = user[..colon];
        return new string(user.Where(char.IsDigit).ToArray());
    }

    /// <summary>True if <paramref name="senderJid"/> is the configured owner (compared by bare user).</summary>
    public static bool IsOwner(string? ownerNumber, string? senderJid)
    {
        var owner = BareUser(FromNumber(ownerNumber));
        var sender = BareUser(senderJid);
        return owner.Length > 0 && owner == sender;
    }

    /// <summary>True if the JID is a group chat (domain "g.us").</summary>
    public static bool IsGroup(string? jid) =>
        !string.IsNullOrEmpty(jid) && jid.Contains("@g.us", StringComparison.OrdinalIgnoreCase);
}
