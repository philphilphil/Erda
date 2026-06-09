using System.ComponentModel;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Agents.Tools;

/// <summary>
/// Exposes the agent's proactive WhatsApp tools: <c>message_me</c> (send Phil a text) and
/// <c>send_image</c> (send Phil an image file, e.g. a browser screenshot). This is the agent-facing
/// side of the outbound path; the error-watch scheduler uses the same <see cref="IWhatsAppSender"/>
/// directly.
/// </summary>
public sealed class NotifyTools(IWhatsAppSender sender, IOptions<WhatsAppOptions> options)
{
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(MessageMe, "message_me"),
        AIFunctionFactory.Create(SendImage, "send_image"),
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

    [Description(
        "Send an image file to Phil (the owner) on WhatsApp — e.g. a screenshot the browser captured. " +
        "Provide the absolute file path (the browser writes screenshots to the media directory) and an " +
        "optional caption. Returns whether it was delivered.")]
    private async Task<string> SendImage(
        [Description("Absolute path to the image file to send.")] string filePath,
        [Description("Optional caption to send with the image.")] string? caption = null)
    {
        var jid = WhatsAppJid.FromNumber(options.Value.OwnerNumber);
        if (string.IsNullOrEmpty(jid))
            return "Cannot send: the WhatsApp owner number is not configured.";
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return $"Cannot send: there is no file at '{filePath}'.";

        var ok = await sender.SendImageAsync(jid, filePath, caption);
        return ok ? "Image delivered to Phil on WhatsApp." : "Failed to send the image (the WhatsApp bridge may be down).";
    }
}
