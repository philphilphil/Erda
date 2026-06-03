using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Verifies the <c>SeedAddTodoPromptGuidance</c> data migration. Unlike the other DB tests (which use
/// EnsureCreated and bypass migrations), these apply real migrations so the migration's Up() runs —
/// covering the append, the active-flag flip, the idempotency guard, and the empty-DB no-op. Applying
/// the migration to a target id requires <see cref="IMigrator"/> so we can seed an active prompt
/// <i>before</i> the seed migration runs (mirroring an existing instance).
/// </summary>
public class AddTodoPromptMigrationTests
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string BeforeSeed = "20260603120100_AddPreScript";

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
    public void Appends_add_todo_to_active_system_prompt_as_new_active_version()
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

        // Apply the seed migration.
        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate();

        using (var db = new ErdaDbContext(options))
        {
            // Exactly one active system version, built from the CURRENT prompt + the add_todo block.
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("CURRENT SYSTEM PROMPT.", active.Content);
            Assert.Contains("add_todo", active.Content);
            Assert.Contains("Calendar/Todos.md", active.Content);
            Assert.Contains("Phil's todo list", active.Content); // apostrophe escaping survived intact

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
    public void Is_noop_when_active_prompt_already_mentions_add_todo()
    {
        var options = NewOptions();

        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.Add(Row(PromptKind.System, "Already documents add_todo here.", active: true));
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate();

        using (var db = new ErdaDbContext(options))
        {
            var system = db.PromptVersions.Where(p => p.Kind == PromptKind.System).ToList();
            Assert.Single(system); // guard prevented a duplicate version
            Assert.Equal("Already documents add_todo here.", system[0].Content);
            Assert.True(system[0].IsActive);
        }
    }

    [Fact]
    public void Is_noop_on_empty_db()
    {
        var options = NewOptions();

        using (var db = new ErdaDbContext(options))
            db.Database.Migrate(); // full migrate incl. the seed, with no prompt rows present

        using (var db = new ErdaDbContext(options))
            Assert.Empty(db.PromptVersions.Where(p => p.Kind == PromptKind.System));
    }
}
