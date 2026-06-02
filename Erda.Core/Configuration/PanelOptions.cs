namespace Erda.Configuration;

/// <summary>
/// Settings for the web control panel (bound from the "Panel" config section). Single-user, LAN-only.
/// Authentication is <b>off by default</b>: when <see cref="Password"/> is blank the panel is open to
/// anyone on the LAN. Set a password to require a cookie login. Credentials are NOT secrets that rotate
/// per request — this is a single shared login for one operator on a home network.
/// </summary>
public sealed class PanelOptions
{
    public const string SectionName = "Panel";

    /// <summary>Login username. Ignored when blank (then only the password is checked).</summary>
    public string Username { get; set; } = "admin";

    /// <summary>Login password. When blank, the panel requires no authentication at all.</summary>
    public string? Password { get; set; }

    /// <summary>True when a password is configured, so the API must be authenticated.</summary>
    public bool AuthRequired => !string.IsNullOrWhiteSpace(Password);
}
