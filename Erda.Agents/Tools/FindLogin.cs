using System.ComponentModel;
using System.Text.Json;
using Erda.Core.Services.OnePassword;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>One item from <c>op item list</c> (id, title, website URLs). No secret fields.</summary>
public sealed record OpItemSummary(string Id, string Title, IReadOnlyList<string> Urls);

/// <summary>One item from <c>op item get</c>: identity, URLs, and whether it carries a TOTP field.</summary>
public sealed record OpItemDetail(string Id, string Title, IReadOnlyList<string> Urls, bool HasTotp);

/// <summary>The <c>op://…</c> references for a login. Never contains secret values.</summary>
public sealed record LoginReferences(string Title, string UsernameRef, string PasswordRef, string? OneTimePasswordRef);

/// <summary>
/// The <c>find_login(domain)</c> tool and its pure helpers. The 1Password <c>Erda</c> vault is the
/// account registry and the allow-list: this lists the vault, matches the page's registrable domain
/// against each item's website, and returns that item's <c>op://…</c> <b>references</b> — never the
/// secret values (those resolve below the LLM via <see cref="SecretInjection"/> at type-time).
///
/// 0 matches → "no login" (fails safe — the vault is the boundary); multiple → an ambiguity result.
/// </summary>
public static class FindLogin
{
    /// <summary>Builds the <c>find_login</c> AIFunction over an <see cref="IOpCli"/> + vault name.</summary>
    public static AIFunction CreateTool(IOpCli cli, string vault)
    {
        async Task<string> FindLoginAsync(
            [Description("The site's domain or full URL, e.g. 'moxfield.com' or the page address.")] string domain,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<OpItemSummary> items;
            try
            {
                var listJson = await cli.RunAsync(["item", "list", "--vault", vault, "--format", "json"], cancellationToken);
                items = ParseList(listJson);
            }
            catch (OpCliException ex)
            {
                return $"Could not reach 1Password to look up a login: {ex.Message}";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Do NOT include ex.Message / the raw JSON — op output can contain secret values.
                return "1Password returned output I couldn't parse while looking up a login.";
            }

            var hits = Match(domain, items);
            if (hits.Count == 0)
                return $"No login found in the {vault} vault for '{domain}'. I cannot sign in to this site.";
            if (hits.Count > 1)
                return $"Multiple logins match '{domain}': {string.Join(", ", hits.Select(h => h.Title))}. " +
                       "Ask which account to use before signing in.";

            OpItemDetail detail;
            try
            {
                var itemJson = await cli.RunAsync(["item", "get", hits[0].Id, "--vault", vault, "--format", "json"], cancellationToken);
                detail = ParseItem(itemJson);
            }
            catch (OpCliException ex)
            {
                return $"Found '{hits[0].Title}' but could not read its details: {ex.Message}";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Generic message only — the item JSON contains the password value; never echo it.
                return $"Found '{hits[0].Title}' but its 1Password entry could not be parsed.";
            }

            var refs = BuildReferences(vault, detail);
            var totp = refs.OneTimePasswordRef is null
                ? "This account has no one-time-code set up."
                : $"If a one-time code / 2FA is requested, type {refs.OneTimePasswordRef}.";

            return $"Found login '{refs.Title}'. Fill the form by typing these 1Password references " +
                   $"verbatim as the field values (they resolve securely): " +
                   $"username = {refs.UsernameRef}; password = {refs.PasswordRef}. {totp} " +
                   "If the site instead shows a captcha or a push/SMS/email challenge, stop and report that you are blocked.";
        }

        return AIFunctionFactory.Create(
            FindLoginAsync,
            new AIFunctionFactoryOptions
            {
                Name = "find_login",
                Description =
                    "Look up a saved login for a site by domain. Returns 1Password references (op://…) to " +
                    "type into the login form — never the secret values. 'No login' means you cannot sign in.",
            });
    }

    /// <summary>Parses <c>op item list --format json</c>: id, title, and any website hrefs.</summary>
    public static IReadOnlyList<OpItemSummary> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<OpItemSummary>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
            var urls = new List<string>();
            if (el.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
                foreach (var u in urlsEl.EnumerateArray())
                    if (u.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
                        urls.Add(h);
            if (id.Length > 0) result.Add(new OpItemSummary(id, title, urls));
        }
        return result;
    }

    /// <summary>Parses <c>op item get --format json</c>: identity, URLs, and TOTP presence.</summary>
    public static OpItemDetail ParseItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";

        var urls = new List<string>();
        if (root.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
            foreach (var u in urlsEl.EnumerateArray())
                if (u.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
                    urls.Add(h);

        var hasTotp = root.TryGetProperty("fields", out var fieldsEl)
            && fieldsEl.ValueKind == JsonValueKind.Array
            && fieldsEl.EnumerateArray().Any(f =>
                f.TryGetProperty("type", out var ty) &&
                string.Equals(ty.GetString(), "OTP", StringComparison.OrdinalIgnoreCase));

        return new OpItemDetail(id, title, urls, hasTotp);
    }

    /// <summary>Items whose website shares a registrable domain with <paramref name="domain"/>.</summary>
    public static IReadOnlyList<OpItemSummary> Match(string domain, IReadOnlyList<OpItemSummary> items) =>
        [.. items.Where(i => i.Urls.Any(u => RegistrableDomain.Matches(u, domain)))];

    /// <summary>Builds the op:// references for an item (username, password, and TOTP if present).</summary>
    public static LoginReferences BuildReferences(string vault, OpItemDetail item) => new(
        Title: item.Title,
        UsernameRef: $"op://{vault}/{item.Id}/username",
        PasswordRef: $"op://{vault}/{item.Id}/password",
        OneTimePasswordRef: item.HasTotp ? $"op://{vault}/{item.Id}/one-time password" : null);
}
