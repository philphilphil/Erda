using System.ComponentModel;
using System.Text;
using Erda.Services;
using Microsoft.Extensions.AI;

namespace Erda.Tools;

/// <summary>
/// The five Obsidian vault function tools exposed to the Erda agent.
/// Each method returns a plain string so the model gets readable results.
/// </summary>
public sealed class ObsidianTools(VaultService vault)
{
    /// <summary>Wrap the methods below as <see cref="AITool"/>s with snake_case names.</summary>
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ListNotes, "list_notes"),
        AIFunctionFactory.Create(ReadNote, "read_note"),
        AIFunctionFactory.Create(SearchNotes, "search_notes"),
        AIFunctionFactory.Create(WriteNote, "write_note"),
        AIFunctionFactory.Create(AppendNote, "append_note"),
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

    [Description("Create a new note or overwrite an existing one with the given content.")]
    private string WriteNote(
        [Description("Vault-relative path, e.g. 'Inbox/New Idea.md'.")] string path,
        [Description("Full Markdown content to write.")] string content)
    {
        vault.WriteNote(path, content);
        return $"Wrote {path} ({content.Length} chars).";
    }

    [Description("Append content to the end of a note, creating it if it does not exist.")]
    private string AppendNote(
        [Description("Vault-relative path, e.g. 'Daily/2026-05-30.md'.")] string path,
        [Description("Markdown content to append.")] string content)
    {
        vault.AppendNote(path, content);
        return $"Appended {content.Length} chars to {path}.";
    }
}
