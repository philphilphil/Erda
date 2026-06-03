namespace Erda.Core.Services;

/// <summary>Fetches a web page and returns its body text (used by the recipe-importer workflow).</summary>
public interface IUrlFetcher
{
    /// <summary>GET the URL and return the response body (capped). Throws on a bad URL or HTTP error.</summary>
    Task<string> FetchAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IUrlFetcher"/> over <see cref="IHttpClientFactory"/>: a browser-ish User-Agent (some
/// sites block default agents), a bounded timeout, and a response-size cap. Network egress is fine —
/// Erda runs on a trusted LAN host.
/// </summary>
public sealed class UrlFetcher(IHttpClientFactory httpClientFactory, ILogger<UrlFetcher> logger) : IUrlFetcher
{
    private const int MaxChars = 600_000;

    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("That doesn't look like a web link (expected http:// or https://).");

        using var client = httpClientFactory.CreateClient(nameof(UrlFetcher));
        client.Timeout = TimeSpan.FromSeconds(20);
        // A real browser User-Agent + Accept headers — many recipe sites 403 a bot-looking agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,de;q=0.8");

        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Couldn't fetch the page (HTTP {(int)response.StatusCode}).");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("Fetched {Url}: {Chars} chars (HTTP {Status}).", uri, body.Length, (int)response.StatusCode);
        return body.Length > MaxChars ? body[..MaxChars] : body;
    }
}
