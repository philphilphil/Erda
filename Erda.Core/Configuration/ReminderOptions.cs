namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the reminder scheduler (bound from the "Reminders" config section). Every minute
/// it reads the reminders note from the vault, fires anything due (verbatim message or agent
/// prompt), and pushes the result to Phil over WhatsApp.
/// </summary>
public sealed class ReminderOptions
{
    public const string SectionName = "Reminders";

    /// <summary>Master switch for the background scheduler. Absent ⇒ off. The note path, timezone and
    /// intervals below are required (no default) only when this is true — see <c>ReminderOptionsValidator</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Vault-relative path to the note that holds the reminder tables. Required when enabled.</summary>
    public string NotePath { get; set; } = "";

    /// <summary>IANA timezone the <c>when</c> column is interpreted in. Required when enabled.</summary>
    public string TimeZone { get; set; } = "";

    /// <summary>How often to check for due reminders. Required when enabled.</summary>
    public TimeSpan PollInterval { get; set; }

    /// <summary>How late a one-shot may still fire after its time (e.g. after downtime). Required when enabled.</summary>
    public TimeSpan OverdueGrace { get; set; }

    /// <summary>Where the run-state sidecar is persisted. Null = a default app-data path.</summary>
    public string? StateFile { get; set; }

    /// <summary>Send Phil a WhatsApp note on a parse or dispatch failure. Absent ⇒ off.</summary>
    public bool NotifyOnError { get; set; }

    /// <summary>
    /// Master switch for scheduled-prompt pre-run context scripts. Absent ⇒ off (a row's script is
    /// ignored and a single warning is logged per process). The two limits below are required only
    /// when this is true.
    /// </summary>
    public bool PreScriptEnabled { get; set; }

    /// <summary>Kill a pre-run script that runs longer than this. Required when PreScriptEnabled.</summary>
    public TimeSpan PreScriptTimeout { get; set; }

    /// <summary>Cap the injected script stdout (chars) to protect the prompt's token budget. Required when PreScriptEnabled.</summary>
    public int PreScriptMaxOutputChars { get; set; }
}
