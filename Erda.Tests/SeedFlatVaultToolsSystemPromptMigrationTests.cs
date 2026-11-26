using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Verifies the <c>SeedFlatVaultToolsSystemPrompt</c> data migration: it replaces the active system
/// prompt (kept as history), seeds on a fresh DB, and no-ops once the active prompt already knows
/// <c>move_note</c>.
/// </summary>
public class SeedFlatVaultToolsSystemPromptMigrationTests
{
    private const string BeforeSeed = "20260808134631_SeedCalendarAndCardPriceSystemPrompt";
    private const string Seed = "20261126093014_SeedFlatVaultToolsSystemPrompt";

    private static DbContextOptions<ErdaDbContext> NewOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), "erda-mig-" + Guid.NewGuid().ToString("N") + ".db");
        return new DbContextOptionsBuilder<ErdaDbContext>().UseSqlite($"Data Source={path}").Options;
    }

    [Fact]
    public void Fresh_db_ends_with_the_flat_vault_prompt_active_and_the_old_one_as_history()
    {
        var options = NewOptions();
        using (var db = new ErdaDbContext(options))
            db.Database.Migrate();

        using (var db = new ErdaDbContext(options))
        {
            var active = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.IsActive);
            Assert.StartsWith("You are Erda,", active.Content);
            foreach (var tool in new[] { "vault_conventions", "edit_note", "write_note", "move_note", "card_price", "create_calendar_event" })
                Assert.Contains(tool, active.Content);
            Assert.DoesNotContain("edit_vault_note", active.Content);
            Assert.DoesNotContain("process_voice_memo", active.Content);
            Assert.Contains("Phil's language", active.Content); // apostrophe escaping survived

            // The calendar/card_price prompt is still there, inactive, for rollback in the panel.
            var previous = db.PromptVersions.Single(p => p.Kind == PromptKind.System && p.Note!.Contains("20260808134631"));
            Assert.False(previous.IsActive);
        }
    }

    [Fact]
    public void Is_noop_when_the_active_prompt_already_mentions_move_note()
    {
        var options = NewOptions();
        using (var db = new ErdaDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(BeforeSeed);
            db.PromptVersions.ExecuteDelete();
            db.PromptVersions.Add(new PromptVersion
            {
                Kind = PromptKind.System, Content = "Hand-written: use move_note to rename.", IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        using (var db = new ErdaDbContext(options))
            db.GetService<IMigrator>().Migrate(Seed);

        using (var db = new ErdaDbContext(options))
        {
            var system = db.PromptVersions.Where(p => p.Kind == PromptKind.System).ToList();
            var only = Assert.Single(system);
            Assert.True(only.IsActive);
            Assert.Equal("Hand-written: use move_note to rename.", only.Content);
        }
    }
}
