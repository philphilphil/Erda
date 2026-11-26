using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>The flat vault tool set on the agent, and <see cref="VaultService.MoveNote"/>'s link rewriting.</summary>
public class VaultToolsTests
{
    private static (VaultService Vault, string Root) NewVault(params (string Path, string Content)[] notes)
    {
        var root = Path.Combine(Path.GetTempPath(), "erda-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (path, content) in notes)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return (new VaultService(Options.Create(new ErdaOptions { VaultPath = root })), root);
    }

    private static AIFunction Tool(VaultService vault, string name) =>
        new ObsidianTools(vault).AsTools().OfType<AIFunction>().Single(f => f.Name == name);

    private static async Task<string> Call(AIFunction f, Dictionary<string, object?> args) =>
        (await f.InvokeAsync(new AIFunctionArguments(args)))?.ToString() ?? "";

    [Fact]
    public void Exposes_the_flat_vault_tool_set()
    {
        var (vault, _) = NewVault();
        var names = new ObsidianTools(vault).AsTools().OfType<AIFunction>().Select(f => f.Name).Order().ToList();
        Assert.Equal(
            new[] { "add_todo", "edit_note", "list_notes", "move_note", "read_note", "search_notes", "vault_conventions", "write_note" },
            names);
    }

    [Fact]
    public void Edit_note_description_pins_the_CriticMarkup_author()
    {
        var (vault, _) = NewVault();
        Assert.Contains("author=\"Erda\"", Tool(vault, "edit_note").Description);
    }

    [Fact]
    public async Task Write_note_refuses_to_overwrite_unless_asked()
    {
        var (vault, root) = NewVault(("Desk/A.md", "original"));
        var write = Tool(vault, "write_note");

        var refused = await Call(write, new() { ["path"] = "Desk/A.md", ["content"] = "new" });
        Assert.Contains("already exists", refused);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "Desk/A.md")));

        await Call(write, new() { ["path"] = "Desk/A.md", ["content"] = "new", ["overwrite"] = true });
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "Desk/A.md")));
    }

    [Fact]
    public async Task Vault_conventions_stacks_agents_md_for_a_note_that_does_not_exist_yet()
    {
        var (vault, _) = NewVault(("AGENTS.md", "ROOT RULES"), ("1 Inbox/AGENTS.md", "INBOX RULES"));
        var text = await Call(Tool(vault, "vault_conventions"), new() { ["path"] = "1 Inbox/New.md" });
        Assert.True(text.IndexOf("ROOT RULES", StringComparison.Ordinal) < text.IndexOf("INBOX RULES", StringComparison.Ordinal));
    }

    [Fact]
    public void Move_note_renames_and_rewrites_bare_path_alias_heading_and_embed_links()
    {
        var (vault, root) = NewVault(
            ("Desk/Kehlmann - Afika.md", "# Afrika"),
            ("Journal/May.md", "Read [[Kehlmann - Afika]] and [[Kehlmann - Afika|the novel]], see [[Kehlmann - Afika#Zitate]]."),
            ("Index.md", "![[Desk/Kehlmann - Afika]] and [[Desk/Kehlmann - Afika.md]] and [[Other]]"),
            ("Other.md", "unrelated [[Somewhere]]"));

        var r = vault.MoveNote("Desk/Kehlmann - Afika.md", "Desk/Kehlmann - Afrika.md");

        Assert.False(File.Exists(Path.Combine(root, "Desk/Kehlmann - Afika.md")));
        Assert.Equal("# Afrika", File.ReadAllText(Path.Combine(root, "Desk/Kehlmann - Afrika.md")));
        Assert.Equal(
            "Read [[Kehlmann - Afrika]] and [[Kehlmann - Afrika|the novel]], see [[Kehlmann - Afrika#Zitate]].",
            File.ReadAllText(Path.Combine(root, "Journal/May.md")));
        Assert.Equal(
            "![[Desk/Kehlmann - Afrika]] and [[Desk/Kehlmann - Afrika.md]] and [[Other]]",
            File.ReadAllText(Path.Combine(root, "Index.md")));
        Assert.Equal("unrelated [[Somewhere]]", File.ReadAllText(Path.Combine(root, "Other.md")));
        Assert.Equal(5, r.LinksUpdated);
        Assert.Equal(2, r.NotesUpdated.Count);
    }

    [Fact]
    public void Move_note_leaves_ambiguous_bare_links_alone()
    {
        // Two notes named "Plan": a bare [[Plan]] does not unambiguously point at the moved one.
        var (vault, root) = NewVault(
            ("A/Plan.md", "a"), ("B/Plan.md", "b"),
            ("Ref.md", "[[Plan]] and [[A/Plan]]"));

        vault.MoveNote("A/Plan.md", "A/Roadmap.md");

        Assert.Equal("[[Plan]] and [[A/Roadmap]]", File.ReadAllText(Path.Combine(root, "Ref.md")));
    }

    [Fact]
    public void Move_note_uses_a_path_link_when_the_new_name_would_be_ambiguous()
    {
        var (vault, root) = NewVault(
            ("A/Draft.md", "a"), ("B/Final.md", "b"),
            ("Ref.md", "[[Draft]]"));

        vault.MoveNote("A/Draft.md", "A/Final.md");

        Assert.Equal("[[A/Final]]", File.ReadAllText(Path.Combine(root, "Ref.md")));
    }

    [Fact]
    public void Move_note_refuses_to_overwrite_and_to_escape_the_vault()
    {
        var (vault, _) = NewVault(("A.md", "a"), ("B.md", "b"));
        Assert.Throws<InvalidOperationException>(() => vault.MoveNote("A.md", "B.md"));
        Assert.Throws<InvalidOperationException>(() => vault.MoveNote("A.md", "../outside.md"));
    }

    [Fact]
    public void Move_note_allows_a_case_only_rename()
    {
        var (vault, root) = NewVault(("note.md", "x"), ("Ref.md", "[[note]]"));

        vault.MoveNote("note.md", "Note.md");

        Assert.Contains("Note.md", Directory.GetFiles(root).Select(Path.GetFileName));
        Assert.Equal("[[Note]]", File.ReadAllText(Path.Combine(root, "Ref.md")));
    }
}
