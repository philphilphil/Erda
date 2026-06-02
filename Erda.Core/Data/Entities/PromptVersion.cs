namespace Erda.Core.Data;

/// <summary>Which prompt a <see cref="PromptVersion"/> belongs to. The two prompts share one table
/// but are independent (separate active row, history, and rollback).</summary>
public static class PromptKind
{
    /// <summary>The orchestrator's system prompt (versioned, with rollback in the UI).</summary>
    public const string System = "system";

    /// <summary>The voice-memo processing prompt (edited save-in-place; no history in the UI).</summary>
    public const string Voice = "voice";
}

/// <summary>
/// A saved version of a prompt. Each <see cref="Kind"/> has exactly one row with
/// <see cref="IsActive"/> = true; that is the prompt the agent (or the voice-memo pipeline) is built
/// from. Saving a new version inserts a row and moves the active flag within that kind; older rows
/// are kept for diff / rollback.
/// </summary>
public sealed class PromptVersion
{
    public int Id { get; set; }

    /// <summary>Which prompt this version belongs to — see <see cref="PromptKind"/>.</summary>
    public string Kind { get; set; } = PromptKind.System;

    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public string? Note { get; set; }
}
