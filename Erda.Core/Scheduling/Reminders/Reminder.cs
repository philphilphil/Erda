namespace Erda.Core.Scheduling;

/// <summary>Whether a reminder is sent verbatim or run through the agent.</summary>
public enum ReminderKind
{
    /// <summary>Send the stored text to Phil verbatim (no model call).</summary>
    Reminder,

    /// <summary>Run the stored text through the Erda agent and send the reply.</summary>
    Prompt,
}

/// <summary>Lifecycle status of a reminder row.</summary>
public enum ReminderStatus
{
    /// <summary>Eligible to fire.</summary>
    Active,

    /// <summary>Manually paused; skipped until set active again.</summary>
    Paused,

    /// <summary>A one-shot that has already fired (or was skipped past its grace window).</summary>
    Done,
}

/// <summary>One parsed reminder row from the vault note. <see cref="Spec"/> is always valid.</summary>
public sealed record Reminder(
    string Id,
    ReminderKind Kind,
    string When,
    string Text,
    ReminderStatus Status,
    WhenSpec Spec);
