namespace Erda.Core.Services.OnePassword;

/// <summary>
/// Resolves a single 1Password secret <b>reference</b> (<c>op://Vault/Item/field</c>) to its
/// current value. Plain fields resolve via <c>op read</c>; a one-time-password field resolves to the
/// current 6-digit TOTP code via <c>op item get --otp</c> and is <b>never cached</b> (codes rotate).
/// Only references inside the configured vault are accepted. The resolved value is never logged.
/// </summary>
public interface IOpSecretResolver
{
    Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}
