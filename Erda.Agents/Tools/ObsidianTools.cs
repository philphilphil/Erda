using System.ComponentModel;
using System.Text;
using Erda.Core.Services;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// The read-side Obsidian vault function tools (plus the conventionless todo append) exposed to the
/// Erda agent. Each method returns a plain string so the model gets readable results. Convention-aware
/// writes live in the vault-editor sub-agent (<c>edit_vault_note</c>), not here.
/// </summary>
public sealed class ObsidianTools(VaultService vault)
{
    /// <summary>Vault-relative note that collects Phil's todos, one Markdown checkbox per line.</summary>
    private const string TodoNote = "Calendar/Todos.md";

    /// <summary>Wrap the methods below as <see cref="AITool"/>s with snake_case names.</summary>
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ListNotes, "list_notes"),
        AIFunctionFactory.Create(ReadNote, "read_note"),
        AIFunctionFactory.Create(SearchNotes, "search_notes"),
        AIFunctionFactory.Create(AddTodo, "add_todo"),
    ];

    [Description("List Markdown (.md) notes in the Obsidian vault, optionally limited to a subfolder.")]
    private string ListNotes(
        [Description("Optional vault-relative subfolder, e.g. 'Projects'. Omit to list the whole vault.")] string? subfolder = null)
    {
        var notes = vault.ListNotes(subfolder);
        return notes.Count == 0 ? "No notes found." : string.Join("\n", notes);
    }

    [Description("Read the full contents of a note from the Obsidian vault.")]
    private string ReadNote(
        [Description("Vault-relative path, e.g. 'Projects/Ideas.md'.")] string path)
        => vault.ReadNote(path);

    [Description("Case-insensitive full-text search across all notes. Returns matching paths with a short snippet.")]
    private string SearchNotes(
        [Description("Text to search for.")] string query)
    {
        var hits = vault.Search(query);
        if (hits.Count == 0)
            return $"No matches for '{query}'.";

        var sb = new StringBuilder();
        foreach (var (path, snippet) in hits)
            sb.AppendLine($"{path}: …{snippet}…");
        return sb.ToString().TrimEnd();
    }

    [Description(
        "Add a single todo item (an unchecked '- [ ] ' checkbox) to Phil's todo list at " +
        "Calendar/Todos.md. Use whenever Phil asks to remember a task: 'todo <thing>', " +
        "'mach mir ein todo dass ...', 'add a todo to ...', 'setz auf meine todo-liste ...'. " +
        "Pass only the task text; the checkbox markup and the destination note are added " +
        "automatically. Match Phil's language for the task text.")]
    private string AddTodo(
        [Description("The task text only, e.g. 'den Müll runterbringen' — no '- [ ]' prefix.")] string task)
    {
        var clean = task.Trim();
        if (clean.Length == 0)
            return "Nothing to add — the todo text was empty.";

        // Make sure the new item lands on its own line even if the file's last line
        // has no trailing newline. A missing or empty file needs no leading newline.
        var existing = vault.Exists(TodoNote) ? vault.ReadNote(TodoNote) : "";
        var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";

        vault.AppendNote(TodoNote, $"{prefix}- [ ] {clean}\n");
        return $"Added todo to {TodoNote}: {clean}";
    }
}
