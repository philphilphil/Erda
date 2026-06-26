using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class VaultServiceTests
{
    private static (VaultService Vault, string Root) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erda-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (new VaultService(Options.Create(new ErdaOptions { VaultPath = dir })), dir);
    }

    // ---- StackConventions ----

    [Fact]
    public void Stacks_root_first_and_nearest_last()
    {
        var (vault, _) = Make();
        vault.WriteNote("AGENTS.md", "ROOT RULES");
        vault.WriteNote("Efforts/AGENTS.md", "EFFORTS RULES");
        vault.WriteNote("Efforts/On/AGENTS.md", "ON RULES");

        var s = vault.StackConventions("Efforts/On/draft.md");

        Assert.True(s.IndexOf("ROOT RULES", StringComparison.Ordinal) < s.IndexOf("EFFORTS RULES", StringComparison.Ordinal));
        Assert.True(s.IndexOf("EFFORTS RULES", StringComparison.Ordinal) < s.IndexOf("ON RULES", StringComparison.Ordinal));
        Assert.Contains("win on conflict", s);
    }

    [Fact]
    public void Heads_each_chunk_with_its_scope()
    {
        var (vault, _) = Make();
        vault.WriteNote("AGENTS.md", "ROOT RULES");
        vault.WriteNote("Efforts/AGENTS.md", "EFFORTS RULES");
        vault.WriteNote("Efforts/On/AGENTS.md", "ON RULES");

        var s = vault.StackConventions("Efforts/On/draft.md");

        Assert.Contains("### Conventions: Efforts/On/", s);
        Assert.Contains("### Conventions:", s); // the root chunk carries a scope header too
    }

    [Fact]
    public void Skips_folders_with_no_AGENTS_file()
    {
        var (vault, _) = Make();
        vault.WriteNote("AGENTS.md", "ROOT RULES");
        vault.WriteNote("Efforts/On/AGENTS.md", "ON RULES");   // no Efforts/AGENTS.md

        var s = vault.StackConventions("Efforts/On/draft.md");

        Assert.Contains("ROOT RULES", s);
        Assert.Contains("ON RULES", s);
        Assert.DoesNotContain("### Conventions: Efforts/\n", s);
    }

    [Fact]
    public void Collects_every_ancestor_for_a_deeply_nested_note()
    {
        var (vault, _) = Make();
        vault.WriteNote("AGENTS.md", "ROOT RULES");
        vault.WriteNote("A/AGENTS.md", "A RULES");
        vault.WriteNote("A/B/AGENTS.md", "B RULES");
        vault.WriteNote("A/B/C/AGENTS.md", "C RULES");

        var s = vault.StackConventions("A/B/C/D/note.md");

        Assert.True(s.IndexOf("ROOT RULES", StringComparison.Ordinal) < s.IndexOf("A RULES", StringComparison.Ordinal));
        Assert.True(s.IndexOf("A RULES", StringComparison.Ordinal) < s.IndexOf("B RULES", StringComparison.Ordinal));
        Assert.True(s.IndexOf("B RULES", StringComparison.Ordinal) < s.IndexOf("C RULES", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_path_escaping_the_root_when_stacking()
    {
        var (vault, _) = Make();
        var ex = Assert.Throws<InvalidOperationException>(() => vault.StackConventions("../outside/note.md"));
        Assert.Contains("escapes the vault root", ex.Message);
    }

    [Fact]
    public void Does_not_walk_dot_folders()
    {
        var (vault, _) = Make();
        vault.WriteNote("AGENTS.md", "ROOT RULES");
        vault.WriteNote(".obsidian/AGENTS.md", "SECRET");

        var s = vault.StackConventions(".obsidian/x.md");

        Assert.DoesNotContain("SECRET", s);
    }

    [Fact]
    public void Returns_a_fallback_when_no_conventions_exist()
    {
        var (vault, _) = Make();
        var s = vault.StackConventions("note.md");

        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.DoesNotContain("### Conventions:", s);
    }

    [Fact]
    public void A_root_level_note_picks_up_the_root_AGENTS_under_the_vault_root_scope()
    {
        var (vault, _) = Make();
        vault.WriteNote("AGENTS.md", "ROOT RULES");

        var s = vault.StackConventions("note.md");

        Assert.Contains("ROOT RULES", s);
        Assert.Contains("### Conventions: (vault root)", s);
    }

    // ---- ReplaceInNote ----

    [Fact]
    public void Replaces_a_unique_match_and_writes_back()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "alpha BETA gamma");

        vault.ReplaceInNote("n.md", "BETA", "delta");

        Assert.Equal("alpha delta gamma", vault.ReadNote("n.md"));
    }

    [Fact]
    public void Throws_when_the_anchor_is_not_found()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "alpha beta");

        var ex = Assert.Throws<InvalidOperationException>(() => vault.ReplaceInNote("n.md", "zeta", "x"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("alpha beta", vault.ReadNote("n.md"));
    }

    [Fact]
    public void Throws_when_the_anchor_is_not_unique()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "x and x");

        var ex = Assert.Throws<InvalidOperationException>(() => vault.ReplaceInNote("n.md", "x", "y"));
        Assert.Contains("not unique", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("surrounding context", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("x and x", vault.ReadNote("n.md"));
    }

    [Fact]
    public void Replaces_a_non_unique_anchor_once_more_context_is_added()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "x and x");

        vault.ReplaceInNote("n.md", "and x", "and y");

        Assert.Equal("x and y", vault.ReadNote("n.md"));
    }

    [Fact]
    public void Replaces_a_multiline_anchor_spanning_a_line_break()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "line one\nline two\nline three");

        vault.ReplaceInNote("n.md", "one\nline two", "ONE\nLINE TWO");

        Assert.Equal("line ONE\nLINE TWO\nline three", vault.ReadNote("n.md"));
    }

    [Fact]
    public void Treats_a_regex_special_anchor_as_a_literal()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "before .*[ after");

        vault.ReplaceInNote("n.md", ".*[", "X");

        Assert.Equal("before X after", vault.ReadNote("n.md"));
    }

    [Fact]
    public void An_empty_newString_deletes_the_anchor_cleanly()
    {
        var (vault, _) = Make();
        vault.WriteNote("n.md", "keep [DELETE ME] keep");

        vault.ReplaceInNote("n.md", "[DELETE ME] ", "");

        Assert.Equal("keep keep", vault.ReadNote("n.md"));
    }

    [Fact]
    public void Rejects_a_path_escaping_the_root_when_replacing()
    {
        var (vault, _) = Make();
        var ex = Assert.Throws<InvalidOperationException>(() => vault.ReplaceInNote("../evil.md", "a", "b"));
        Assert.Contains("escapes the vault root", ex.Message);
    }

    [Fact]
    public void Throws_when_the_note_does_not_exist()
    {
        var (vault, _) = Make();
        Assert.Throws<FileNotFoundException>(() => vault.ReplaceInNote("missing.md", "a", "b"));
    }
}
