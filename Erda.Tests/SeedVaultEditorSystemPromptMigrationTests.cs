using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Verifies the <c>SeedVaultEditorSystemPrompt</c> data migration (sibling of
/// <see cref="BrowserPromptMigrationTests"/>). Unlike the earlier prompt seeds, this one <i>replaces</i>
/// the whole system prompt rather than appending a block, so its cases differ: it seeds even on an
/// empty DB, replaces a stale prompt, and no-ops only once the active prompt already mentions
/// <c>edit_vault_note</c>. Migrates to just before the seed, plants rows, then pins to the seed.
/// </summary>
public class SeedVaultEditorSystemPromptMigrationTests
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string BeforeSeed = "20260625193554_DropReminderDirectToCodex";

    /// <summary>The migration under test. Pinned in case later migrations are added after it.</summary>
    private const string Seed = "20260626113823_SeedVaultEditorSystemPrompt";

    private static DbContextOptions<ErdaDbContext> NewOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), "erda-mig-" + Guid.NewGuid().ToString("N") + ".db");
        return new DbContextOptionsBuilder<ErdaDbContext>().UseSqlite($"Data Source={path}").Options;
    }

    private static PromptVersion Row(string kind, string content, bool active) => new()
    {
        Kind = kind,
        Content = content,
        IsActive = active,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Replaces_the_stale_system_prompt_with_a_new_active_version()
    {
        var options = NewOptions();

        // Plant a stale codex-era prompt (no edit_vault_note) as the active one, plus a voice prompt.
        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.Add(Row(PromptKind.System, "STALE: routes to consult_codex and delegate_vault_task.", active: true));
            db.PromptVersions.Add(Row(PromptKind.Voice, "VOICE PROMPT", active: true));
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            // Exactly one active system version: the new vault-editor prompt.
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("You are Erda,", active.Content);
            Assert.Contains("edit_vault_note", active.Content);
            Assert.DoesNotContain("consult_codex", active.Content);
            Assert.Contains("don't guess", active.Content); // apostrophe escaping survived intact

            // The stale prompt was deactivated but kept as history (rollback in the panel).
            Assert.False(db.PromptVersions.Single(p => p.Content.StartsWith("STALE:")).IsActive);

            // The voice prompt is untouched.
            var voice = db.PromptVersions.Single(p => p.Kind == PromptKind.Voice);
            Assert.True(voice.IsActive);
            Assert.Equal("VOICE PROMPT", voice.Content);
        }
    }

    [Fact]
    public void Is_noop_when_active_prompt_already_mentions_edit_vault_note()
    {
        var options = NewOptions();

        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.Add(Row(PromptKind.System, "Already documents edit_vault_note here.", active: true));
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var system = db.PromptVersions.Where(p => p.Kind == PromptKind.System).ToList();
            Assert.Single(system); // guard prevented a duplicate version
            Assert.Equal("Already documents edit_vault_note here.", system[0].Content);
            Assert.True(system[0].IsActive);
        }
    }

    [Fact]
    public void Seeds_the_prompt_on_an_empty_db()
    {
        var options = NewOptions();

        // Unlike the append-style seeds, this one DOES seed an empty DB, so a fresh prod instance
        // comes up with a working system prompt instead of none.
        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("You are Erda,", active.Content);
            Assert.Contains("edit_vault_note", active.Content);
        }
    }
}
