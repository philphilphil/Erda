using System.ComponentModel;
using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using Erda.Core.Configuration;
using Erda.Core.WhatsApp;

namespace Erda.Agents.Tools;

/// <summary>
/// Exposes a single tool, <c>message_me</c>, that lets Erda proactively send Phil a WhatsApp
/// message (via the bridge). This is the agent-facing side of the outbound path; the error-watch
/// scheduler uses the same <see cref="IWhatsAppSender"/> directly.
/// </summary>
public sealed class NotifyTools(IWhatsAppSender sender, IOptions<WhatsAppOptions> options)
{
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(MessageMe, "message_me"),
    ];

    [Description(
        "Proactively send a WhatsApp message to Phil (the owner) — e.g. a reminder, a confirmation, " +
        "or something worth surfacing right now. Returns whether it was delivered.")]
    private async Task<string> MessageMe(
        [Description("The message text to send to Phil over WhatsApp.")] string text)
    {
        var jid = WhatsAppJid.FromNumber(options.Value.OwnerNumber);
        if (string.IsNullOrEmpty(jid))
            return "Cannot send: the WhatsApp owner number is not configured.";
        if (string.IsNullOrWhiteSpace(text))
            return "Cannot send an empty message.";

        var ok = await sender.SendAsync(jid, text);
        return ok ? "Delivered to Phil on WhatsApp." : "Failed to send (the WhatsApp bridge may be down).";
    }
}
