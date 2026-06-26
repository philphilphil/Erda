using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Verifies the <c>SeedBrowserPromptGuidance</c> data migration (sibling of
/// <see cref="AddTodoPromptMigrationTests"/>). Migrates up to the migration <i>before</i> the seed,
/// plants an active prompt (mirroring an existing instance), then runs the seed — covering the append,
/// the active-flag flip, the idempotency guard, and the empty-DB no-op. Planting after the prior
/// migration means only the browser seed runs against the planted rows, isolating it from the
/// add_todo seed.
/// </summary>
public class BrowserPromptMigrationTests
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string BeforeSeed = "20260610040519_DropConfigOverrides";

    /// <summary>The migration under test. Pinned (not a full <c>Migrate()</c>) so later seed
    /// migrations — which rewrite/replace the system prompt — don't run here and skew the assertions.</summary>
    private const string Seed = "20260610120000_SeedBrowserPromptGuidance";

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
    public void Appends_browse_web_to_active_system_prompt_as_new_active_version()
    {
        var options = NewOptions();

        // Bring the schema up to just before the seed, then plant an existing system prompt + voice prompt.
        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.Add(Row(PromptKind.System, "OLD SYSTEM PROMPT.", active: false));
            db.PromptVersions.Add(Row(PromptKind.System, "CURRENT SYSTEM PROMPT.", active: true));
            db.PromptVersions.Add(Row(PromptKind.Voice, "VOICE PROMPT", active: true));
            db.SaveChanges();
        }

        // Apply the seed migration (pinned, so later prompt-rewriting seeds don't run).
        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            // Exactly one active system version, built from the CURRENT prompt + the browser block.
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("CURRENT SYSTEM PROMPT.", active.Content);
            Assert.Contains("browse_web", active.Content);
            Assert.Contains("send_image", active.Content);
            Assert.Contains("Don't use", active.Content); // apostrophe escaping survived intact

            // The previously-active prompt was deactivated; the older inactive one is unaffected.
            Assert.False(db.PromptVersions.Single(p => p.Content == "CURRENT SYSTEM PROMPT.").IsActive);
            Assert.False(db.PromptVersions.Single(p => p.Content == "OLD SYSTEM PROMPT.").IsActive);

            // The voice prompt is untouched.
            var voice = db.PromptVersions.Single(p => p.Kind == PromptKind.Voice);
            Assert.True(voice.IsActive);
            Assert.Equal("VOICE PROMPT", voice.Content);
        }
    }

    [Fact]
    public void Is_noop_when_active_prompt_already_mentions_browse_web()
    {
        var options = NewOptions();

        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.Add(Row(PromptKind.System, "Already documents browse_web here.", active: true));
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var system = db.PromptVersions.Where(p => p.Kind == PromptKind.System).ToList();
            Assert.Single(system); // guard prevented a duplicate version
            Assert.Equal("Already documents browse_web here.", system[0].Content);
            Assert.True(system[0].IsActive);
        }
    }

    [Fact]
    public void Is_noop_on_empty_db()
    {
        var options = NewOptions();

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed); // up to the browser seed, with no prompt rows present

        using (var db = new ErdaDbContext(options))
            Assert.Empty(db.PromptVersions.Where(p => p.Kind == PromptKind.System));
    }
}
