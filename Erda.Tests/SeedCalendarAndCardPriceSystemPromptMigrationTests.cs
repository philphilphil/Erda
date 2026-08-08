using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Verifies the <c>SeedCalendarAndCardPriceSystemPrompt</c> data migration (sibling of
/// <see cref="SeedVaultEditorSystemPromptMigrationTests"/>). Like that one it <i>replaces</i> the whole
/// system prompt rather than appending a block, so the cases are the same shape: it seeds even on an
/// empty DB, replaces the current prompt, and no-ops only once the active prompt already mentions
/// <c>card_price</c> (Phil may paste the same text into the control panel before deploying). Migrates
/// to just before the seed, plants rows, then pins to the seed.
/// </summary>
public class SeedCalendarAndCardPriceSystemPromptMigrationTests
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string BeforeSeed = "20260729164511_AddVoiceMemoSourceAndTranscript";

    /// <summary>The migration under test. Pinned in case later migrations are added after it.</summary>
    private const string Seed = "20260808134631_SeedCalendarAndCardPriceSystemPrompt";

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
    public void Replaces_the_current_system_prompt_with_a_new_active_version()
    {
        var options = NewOptions();

        // Plant the current (pre-migration) prompt as the active one, plus an older inactive version and
        // a voice prompt. The earlier vault-editor seed has already planted its own active prompt by this
        // point, so clear the table first — the planted sentinels are then the only rows this seed sees.
        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.ExecuteDelete();
            db.PromptVersions.Add(Row(PromptKind.System, "OLD: an even earlier system prompt.", active: false));
            db.PromptVersions.Add(Row(PromptKind.System, "CURRENT: routes to browse_web, knows no calendar.", active: true));
            db.PromptVersions.Add(Row(PromptKind.Voice, "VOICE PROMPT", active: true));
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            // Exactly one active system version: the new calendar/card_price prompt.
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("You are Erda,", active.Content);
            Assert.Contains("card_price", active.Content);
            Assert.Contains("create_calendar_event", active.Content);
            Assert.Contains("create_apple_reminder", active.Content);
            Assert.DoesNotContain("browse_web", active.Content);
            Assert.Contains("don't guess", active.Content); // apostrophe escaping survived intact

            // The previous prompt was deactivated but kept as history (rollback in the panel); the
            // older inactive one is unaffected.
            Assert.False(db.PromptVersions.Single(p => p.Content.StartsWith("CURRENT:")).IsActive);
            Assert.False(db.PromptVersions.Single(p => p.Content.StartsWith("OLD:")).IsActive);

            // The voice prompt is untouched.
            var voice = db.PromptVersions.Single(p => p.Kind == PromptKind.Voice);
            Assert.True(voice.IsActive);
            Assert.Equal("VOICE PROMPT", voice.Content);
        }
    }

    [Fact]
    public void Is_noop_when_active_prompt_already_mentions_card_price()
    {
        var options = NewOptions();

        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.ExecuteDelete();
            db.PromptVersions.Add(Row(PromptKind.System, "Already documents card_price here.", active: true));
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var system = db.PromptVersions.Where(p => p.Kind == PromptKind.System).ToList();
            Assert.Single(system); // guard prevented a duplicate version
            Assert.Equal("Already documents card_price here.", system[0].Content);
            Assert.True(system[0].IsActive);
        }
    }

    [Fact]
    public void Seeds_the_prompt_on_an_empty_db()
    {
        var options = NewOptions();

        // Nothing planted: the whole chain runs, the vault-editor seed included, and this migration is
        // the last word — a fresh prod instance comes up on the current prompt.
        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("You are Erda,", active.Content);
            Assert.Contains("card_price", active.Content);
        }
    }

    [Fact]
    public void Deactivates_but_keeps_the_prompt_seeded_by_the_previous_migration()
    {
        var options = NewOptions();

        // The realistic upgrade path, with no hand-planted rows: the vault-editor seed is the active
        // prompt after BeforeSeed, and this migration must supersede it without dropping it.
        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            var previous = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.Contains("edit_vault_note", previous.Content);
            Assert.DoesNotContain("card_price", previous.Content);
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var system = db.PromptVersions.Where(p => p.Kind == PromptKind.System).OrderBy(p => p.Id).ToList();
            Assert.Equal(2, system.Count);            // the old version is retained, not overwritten
            Assert.False(system[0].IsActive);         // ... and deactivated
            Assert.Contains("browse_web", system[0].Content);
            Assert.True(system[1].IsActive);
            Assert.Contains("card_price", system[1].Content);
        }
    }
}
