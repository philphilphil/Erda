using System.ComponentModel;
using System.Text;
using Erda.Core.Services;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// The Obsidian vault tools exposed to the Erda agent: read, search, list, the vault's own editing
/// conventions, surgical edits, new notes, rename/move (with backlink rewriting) and the todo append.
/// Each method returns a plain string so the model gets readable results. Errors come back as text, not
/// exceptions, so the model can correct itself. Everything is confined to the vault root by
/// <see cref="VaultService"/>.
/// </summary>
public sealed class ObsidianTools(VaultService vault)
{
    /// <summary>Vault-relative note that collects Phil's todos, one Markdown checkbox per line.</summary>
    private const string TodoNote = "Calendar/Todos.md";

    /// <summary>The fixed CriticMarkup author name Erda signs its marks with. Pinned (not the model's
    /// per-run pick) so it stays consistent: the vault's AGENTS.md thread rules ("never reply to
    /// yourself") key on the author name.</summary>
    internal const string AuthorName = "Erda";

    /// <summary>Wrap the methods below as <see cref="AITool"/>s with snake_case names.</summary>
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ListNotes, "list_notes"),
        AIFunctionFactory.Create(ReadNote, "read_note"),
        AIFunctionFactory.Create(SearchNotes, "search_notes"),
        AIFunctionFactory.Create(VaultConventions, "vault_conventions"),
        AIFunctionFactory.Create(EditNote, "edit_note"),
        AIFunctionFactory.Create(WriteNote, "write_note"),
        AIFunctionFactory.Create(MoveNote, "move_note"),
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
    {
        try { return vault.ReadNote(path); }
        catch (Exception ex) { return $"Cannot read {path}: {ex.Message}"; }
    }

    [Description(
        "Get the vault's editing conventions (its AGENTS.md files, root first, nearest folder last) that " +
        "apply to a note's location. Call this BEFORE creating, editing, reviewing or moving a note, once " +
        "per folder per conversation, and follow what it says. Works for a path that does not exist yet.")]
    private string VaultConventions(
        [Description("Vault-relative path of the note (existing or about to be created).")] string path)
    {
        try { return vault.StackConventions(path); }
        catch (Exception ex) { return $"Cannot resolve conventions for {path}: {ex.Message}"; }
    }

    [Description(
        "Surgically edit an existing note: replace one exact piece of text with new text. The text to " +
        "replace must occur in the note exactly once (include surrounding context to make it unique). " +
        "Use for fixes, rewrites of a passage, appending under a heading, and review passes. Read the note " +
        "first. A review or comment pass adds CriticMarkup in the note itself; sign every mark with the " +
        "author name \"" + AuthorName + "\", e.g. {author=\"" + AuthorName + "\">>…<<}, ignoring any guidance to " +
        "choose your own name. " +
        "Marks signed with other names are other reviewers.")]
    private string EditNote(
        [Description("Vault-relative path of the note.")] string path,
        [Description("Exact text to replace; must occur in the note exactly once.")] string oldString,
        [Description("Replacement text.")] string newString)
    {
        try
        {
            vault.ReplaceInNote(path, oldString, newString);
            return $"Edited {path}.";
        }
        catch (Exception ex) { return $"Could not edit {path}: {ex.Message}"; }
    }

    [Description(
        "Create a new note with the given Markdown content (captures default to '1 Inbox/'). Refuses to " +
        "replace an existing note unless overwrite is true; to change an existing note prefer edit_note. " +
        "Only overwrite when Phil asked to rewrite the whole note.")]
    private string WriteNote(
        [Description("Vault-relative path ending in .md, e.g. '1 Inbox/New Idea.md'.")] string path,
        [Description("Full Markdown content.")] string content,
        [Description("Replace the note if it already exists. Default false.")] bool overwrite = false)
    {
        try
        {
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                return $"Could not write {path}: the path must end in .md.";
            if (!overwrite && vault.Exists(path))
                return $"Could not write {path}: a note already exists there. Use edit_note, or overwrite=true to replace it.";
            vault.WriteNote(path, content);
            return $"Wrote {path} ({content.Length} chars).";
        }
        catch (Exception ex) { return $"Could not write {path}: {ex.Message}"; }
    }

    [Description(
        "Rename or move a note, and rewrite every [[wikilink]] in the vault that points to it. Use for " +
        "fixing a typo in a note's name or filing it into another folder. Never overwrites an existing note.")]
    private string MoveNote(
        [Description("Current vault-relative path, e.g. 'Desk/Old Name.md'.")] string fromPath,
        [Description("New vault-relative path, e.g. 'Desk/New Name.md'.")] string toPath)
    {
        try
        {
            var r = vault.MoveNote(fromPath, toPath);
            if (r.LinksUpdated == 0)
                return $"Moved {r.From} to {r.To}. No links pointed to it.";
            return $"Moved {r.From} to {r.To} and rewrote {r.LinksUpdated} link(s) in {r.NotesUpdated.Count} note(s): " +
                   string.Join(", ", r.NotesUpdated.Take(20)) + (r.NotesUpdated.Count > 20 ? ", …" : "") + ".";
        }
        catch (Exception ex) { return $"Could not move {fromPath}: {ex.Message}"; }
    }

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
