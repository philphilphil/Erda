using Erda.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Services;

/// <summary>Sends a WhatsApp text message to a JID by calling the bridge's <c>/send</c> endpoint.</summary>
public interface IWhatsAppSender
{
    Task<bool> SendAsync(string toJid, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP implementation of <see cref="IWhatsAppSender"/>: POSTs <c>{ to, text }</c> to the bridge's
/// <c>/send</c> with the shared-secret header. The bridge owns the actual WhatsApp socket.
/// </summary>
public sealed class WhatsAppSender(
    HttpClient http,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppSender> logger) : IWhatsAppSender
{
    public async Task<bool> SendAsync(string toJid, string text, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BridgeUrl))
        {
            logger.LogWarning("WhatsApp bridge URL is not configured; cannot send message.");
            return false;
        }

        var url = $"{o.BridgeUrl.TrimEnd('/')}/send";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { to = toJid, text }),
        };
        request.Headers.TryAddWithoutValidation("X-Bridge-Secret", o.SharedSecret);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Bridge /send returned {Status} when sending to {To}.", (int)response.StatusCode, toJid);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to POST to the WhatsApp bridge at {Url}.", url);
            return false;
        }
    }
}
