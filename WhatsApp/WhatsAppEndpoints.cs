using System.Security.Cryptography;
using System.Text;
using Erda.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.WhatsApp;

/// <summary>
/// Maps the inbound bridge endpoint <c>POST /channel/whatsapp/in</c>. The endpoint authenticates
/// with the shared secret, returns <c>202</c> immediately, and hands the message to the worker
/// queue for async processing (agent turns can take 10–30 s).
/// </summary>
public static class WhatsAppEndpoints
{
    public static void MapWhatsAppChannel(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WhatsApp");

        if (!opts.Enabled)
        {
            log.LogInformation("WhatsApp channel disabled (WhatsApp:Enabled=false); inbound endpoint not mapped.");
            return;
        }

        log.LogInformation("WhatsApp channel enabled; mapping POST /channel/whatsapp/in (owner={Owner}).",
            WhatsAppJid.BareUser(WhatsAppJid.FromNumber(opts.OwnerNumber)) is { Length: > 0 } u ? "set" : "MISSING");

        app.MapPost("/channel/whatsapp/in", async (HttpContext ctx, WhatsAppInboundQueue queue, IOptions<WhatsAppOptions> o, ILoggerFactory lf) =>
        {
            var inLog = lf.CreateLogger("WhatsAppInbound");
            if (!SecretOk(ctx, o.Value.SharedSecret))
                return Results.Unauthorized();

            InboundMessage? msg;
            try
            {
                msg = await ctx.Request.ReadFromJsonAsync<InboundMessage>();
            }
            catch (Exception ex)
            {
                inLog.LogWarning(ex, "Rejected malformed inbound WhatsApp payload.");
                return Results.BadRequest();
            }

            if (msg is null || string.IsNullOrEmpty(msg.From))
                return Results.BadRequest();

            await queue.EnqueueAsync(msg);
            return Results.Accepted();
        });
    }

    private static bool SecretOk(HttpContext ctx, string expected)
    {
        if (string.IsNullOrEmpty(expected))
            return true; // no secret configured -> allow (local dev only)
        var provided = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Bridge-Secret"].ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return provided.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(provided, expectedBytes);
    }
}
