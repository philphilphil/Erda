using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <summary>
    /// Data migration: propagate the new <c>add_todo</c> tool guidance into the live system prompt.
    /// There is no longer a code-baked default that re-seeds at runtime, so existing instances would
    /// never learn about the tool. This appends the guidance block to the <b>current active</b> system
    /// prompt (preserving any panel edits) and saves it as a new active <see cref="PromptVersion"/> —
    /// exactly what hitting "Save" in the control panel would do. No-op on a fresh/empty DB (no active
    /// system row) and skipped if the active prompt already mentions <c>add_todo</c> (idempotent).
    /// </summary>
    public partial class SeedAddTodoPromptGuidance : Migration
    {
        private const string Marker = "Append add_todo tool guidance (migration 20260603130000)";

        /// <summary>The guidance appended to the system prompt. Mirrors the add_todo tool's description.</summary>
        private const string AddTodoBlock =
            "add_todo: append one task to Phil's todo list (Calendar/Todos.md). Use it whenever Phil\n" +
            "phrases something as a todo/task — \"todo <thing>\", \"mach mir ein todo dass …\", \"add a todo\n" +
            "to …\", \"setz auf meine todo-liste …\". Pass only the task text (no checkbox markup); keep it\n" +
            "in Phil's language. This is the right tool for short tasks — don't create an inbox note for them.";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite string-literal escaping: double any single quotes in the block.
            var block = AddTodoBlock.Replace("'", "''");
            // EF Core stores DateTimeOffset as TEXT in this exact shape; match it so it round-trips.
            var nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

            // 1. Insert a new active version = current active system prompt + a blank line + the block.
            //    Guards: an active system row must exist, and it must not already mention add_todo.
            migrationBuilder.Sql($@"
INSERT INTO PromptVersions (Kind, Content, CreatedAtUtc, IsActive, Note)
SELECT 'system',
       Content || char(10) || char(10) || '{block}',
       '{nowUtc}',
       1,
       '{Marker}'
FROM PromptVersions
WHERE Kind = 'system'
  AND Id = (SELECT MAX(Id) FROM PromptVersions WHERE Kind = 'system' AND IsActive = 1)
  AND Content NOT LIKE '%add_todo%';");

            // 2. Deactivate the prior active system version, leaving only the new one active.
            //    No-op when step 1 inserted nothing (then there is only one active system row).
            migrationBuilder.Sql(@"
UPDATE PromptVersions
SET IsActive = 0
WHERE Kind = 'system'
  AND IsActive = 1
  AND Id < (SELECT MAX(Id) FROM PromptVersions WHERE Kind = 'system' AND IsActive = 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the version this migration added, then re-activate the previous newest system
            // version if (and only if) nothing is active — restoring the pre-migration active prompt.
            migrationBuilder.Sql($@"
DELETE FROM PromptVersions
WHERE Kind = 'system' AND Note = '{Marker}';");

            migrationBuilder.Sql(@"
UPDATE PromptVersions
SET IsActive = 1
WHERE Kind = 'system'
  AND Id = (SELECT MAX(Id) FROM PromptVersions WHERE Kind = 'system')
  AND NOT EXISTS (SELECT 1 FROM PromptVersions WHERE Kind = 'system' AND IsActive = 1);");
        }
    }
}
