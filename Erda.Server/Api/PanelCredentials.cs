using System.Security.Cryptography;
using System.Text;
using Erda.Configuration;

namespace Erda.Api;

/// <summary>
/// Pure credential check for the panel login, split out of the auth endpoint so it can be unit-tested
/// without the cookie/sign-in machinery. The password is compared in constant time; the username is
/// only checked when one is configured.
/// </summary>
public static class PanelCredentials
{
    public static bool IsValid(PanelOptions panel, string? username, string? password)
    {
        var passwordOk = FixedTimeEquals(password, panel.Password);
        var usernameOk = string.IsNullOrWhiteSpace(panel.Username)
            || string.Equals(username, panel.Username, StringComparison.Ordinal);
        return passwordOk && usernameOk;
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        var ab = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        // Length may leak (single-user LAN secret); the byte comparison itself is constant-time.
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
