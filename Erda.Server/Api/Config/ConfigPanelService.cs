using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Server.Api;

/// <summary>
/// Backs the (read-only) Config screen. Config is env-only and validated/applied at startup, so there
/// is nothing to edit here — change <c>.env</c> and restart. This surfaces the effective, loaded
/// values grouped for display, so the operator can confirm what the container actually booted with
/// without shell access. Secrets are shown as a presence flag, never echoed.
/// </summary>
public sealed class ConfigPanelService(
    IOptions<ErdaOptions> erda,
    IOptions<WhatsAppOptions> whatsApp,
    IOptions<SeqOptions> seq,
    IOptions<ErrorWatchOptions> errorWatch,
    IOptions<ChatHealthOptions> chatHealth,
    IOptions<ReminderOptions> reminders,
    IOptions<ObservabilityOptions> observability,
    IOptions<UploadOptions> upload,
    IOptions<AppleBridgeOptions> appleBridge)
{
    private static string Show(object? v) => v?.ToString() is { Length: > 0 } s ? s : "(not set)";
    private static string Secret(string? v) => string.IsNullOrWhiteSpace(v) ? "(not set)" : "(set)";

    /// <summary>The effective configuration, grouped for the read-only panel.</summary>
    public IReadOnlyList<ConfigItemDto> GetItems()
    {
        var e = erda.Value;
        var w = whatsApp.Value;
        var s = seq.Value;
        var ew = errorWatch.Value;
        var ch = chatHealth.Value;
        var r = reminders.Value;
        var o = observability.Value;
        var up = upload.Value;
        var ab = appleBridge.Value;

        return
        [
            new("Vault & data", "Vault path", Show(e.VaultPath)),
            new("Vault & data", "Database path", Show(e.DbPath)),

            new("Model & reasoning", "Chat base URL", Show(e.ChatBaseUrl)),
            new("Model & reasoning", "Chat model", Show(e.ChatModel)),
            new("Model & reasoning", "Chat reasoning effort", Show(e.ChatReasoningEffort)),
            new("Model & reasoning", "Transcribe model", Show(e.TranscribeModel)),

            new("WhatsApp", "Enabled", Show(w.Enabled)),
            new("WhatsApp", "Owner number", Show(w.OwnerNumber)),
            new("WhatsApp", "Bridge URL", Show(w.BridgeUrl)),
            new("WhatsApp", "Shared secret", Secret(w.SharedSecret)),

            new("Seq", "Server URL", Show(s.ServerUrl)),
            new("Seq", "API key", Secret(s.ApiKey)),

            new("Error watch", "Enabled", Show(ew.Enabled)),
            new("Error watch", "Poll interval", Show(ew.PollInterval)),
            new("Error watch", "Min level", Show(ew.MinLevel)),
            new("Error watch", "Max alerts/poll", Show(ew.MaxAlertsPerPoll)),

            new("Chat health", "Enabled", Show(ch.Enabled)),
            new("Chat health", "Check interval", Show(ch.CheckInterval)),
            new("Chat health", "Probe timeout", Show(ch.Timeout)),
            new("Chat health", "Re-alert after", Show(ch.ReAlertAfter)),

            new("Reminders", "Enabled", Show(r.Enabled)),
            new("Reminders", "Poll interval", Show(r.PollInterval)),
            new("Reminders", "Timezone", Show(r.TimeZone)),

            new("Observability", "Tracing enabled", Show(o.Enabled)),
            new("Observability", "Capture message content", Show(o.CaptureMessageContent)),

            new("Upload", "Enabled", Show(up.Enabled)),
            new("Upload", "Bearer token", Secret(up.ApiKey)),
            new("Upload", "Max upload MB", Show(up.MaxUploadMb)),

            new("Apple Reminders bridge", "Enabled", Show(ab.Enabled)),
            new("Apple Reminders bridge", "Base URL", Show(ab.BaseUrl)),
            new("Apple Reminders bridge", "Bridge API key", Secret(ab.ApiKey)),
            new("Apple Reminders bridge", "Timeout (s)", Show(ab.TimeoutSeconds)),
        ];
    }
}
