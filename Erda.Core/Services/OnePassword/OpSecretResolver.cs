using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services.OnePassword;

/// <inheritdoc />
public sealed class OpSecretResolver(IOpCli cli, IOptions<BrowserOptions> options, ILogger<OpSecretResolver> logger)
    : IOpSecretResolver
{
    private readonly string _vault = options.Value.OnePasswordVault;

    public async Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        var (vault, item, field) = Parse(reference);

        // Defense in depth: the service-account token is already scoped to one vault, but refuse any
        // reference that names a different vault so a prompt-injected ref can't even be attempted.
        if (!string.Equals(vault, _vault, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing 1Password reference outside the '{_vault}' vault.");

        // TOTP must be re-resolved every use (codes rotate every 30s) and is fetched with the
        // dedicated --otp flag, which returns just the current 6-digit code.
        string value = IsOneTimePassword(field)
            ? await cli.RunAsync(["item", "get", item, "--vault", _vault, "--otp"], cancellationToken)
            : await cli.RunAsync(["read", reference], cancellationToken);

        value = value.Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"1Password returned no value for reference {reference}.");

        // Log that we resolved a reference — never the value, never gated on the capture flag.
        logger.LogInformation("Resolved 1Password reference {Reference} ({Kind}).",
            reference, IsOneTimePassword(field) ? "totp" : "field");
        return value;
    }

    /// <summary>Splits <c>op://Vault/Item/Field</c>. Field may contain spaces ("one-time password").</summary>
    private static (string Vault, string Item, string Field) Parse(string reference)
    {
        const string scheme = "op://";
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith(scheme, StringComparison.Ordinal))
            throw new InvalidOperationException($"Not a 1Password reference: '{reference}'.");

        var parts = reference[scheme.Length..].Split('/', 3);
        if (parts.Length < 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Malformed 1Password reference: '{reference}'. Expected op://Vault/Item/field.");

        return (parts[0], parts[1], parts[2]);
    }

    // Substring match (otp/totp) is a deliberate, acceptable trade-off for the single-user vault: a
    // field literally named e.g. "depot" would false-positive, but conventional 1Password field names
    // make that a non-concern.
    private static bool IsOneTimePassword(string field) =>
        field.Equals("one-time password", StringComparison.OrdinalIgnoreCase)
        || field.Contains("otp", StringComparison.OrdinalIgnoreCase)
        || field.Contains("totp", StringComparison.OrdinalIgnoreCase);
}
