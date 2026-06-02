namespace Erda.Core.Data;

/// <summary>
/// A saved version of the system prompt. Exactly one row has <see cref="IsActive"/> = true; that
/// is the prompt the agent is built from at startup. Saving a new version inserts a row and moves
/// the active flag; older rows are kept for diff / rollback.
/// </summary>
public sealed class PromptVersion
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public string? Note { get; set; }
}
