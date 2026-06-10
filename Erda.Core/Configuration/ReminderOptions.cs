namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the reminder scheduler (bound from the "Reminders" config section). Every minute
/// it reads the reminders note from the vault, fires anything due (verbatim message or agent
/// prompt), and pushes the result to Phil over WhatsApp.
/// </summary>
public sealed class ReminderOptions
{
    public const string SectionName = "Reminders";

    /// <summary>Master switch for the background scheduler. Absent ⇒ off; enable explicitly in .env.</summary>
    public bool Enabled { get; set; }

    /// <summary>Vault-relative path to the note that holds the reminder tables.</summary>
    public string NotePath { get; set; } = "Atlas/AI/Erda/Reminders.md";

    /// <summary>IANA timezone the <c>when</c> column is interpreted in.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>How often to check for due reminders.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How late a one-shot may still fire after its time (e.g. after downtime).</summary>
    public TimeSpan OverdueGrace { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Where the run-state sidecar is persisted. Null = a default app-data path.</summary>
    public string? StateFile { get; set; }

    /// <summary>When true, send Phil a WhatsApp note on a parse or dispatch failure.</summary>
    public bool NotifyOnError { get; set; } = true;

    /// <summary>
    /// Master switch for scheduled-prompt pre-run context scripts. When false, a row's script is
    /// ignored (treated as none) and a single warning is logged per process.
    /// </summary>
    public bool PreScriptEnabled { get; set; } = true;

    /// <summary>Kill a pre-run script that runs longer than this.</summary>
    public TimeSpan PreScriptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cap the injected script stdout (chars) to protect the prompt's token budget.</summary>
    public int PreScriptMaxOutputChars { get; set; } = 8000;
}
