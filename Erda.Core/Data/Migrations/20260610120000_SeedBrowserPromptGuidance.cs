using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <summary>
    /// Data migration: propagate the <c>browse_web</c> (Playwright) browser + screenshot capability
    /// into the live system prompt. The browser feature shipped with no prompt update, so existing
    /// instances never learned they have a real browser — they reach for consult_codex (no browser)
    /// and refuse screenshots. This appends a guidance block to the <b>current active</b> system prompt
    /// (preserving any panel edits) and saves it as a new active <see cref="PromptVersion"/> — exactly
    /// what hitting "Save" in the control panel would do. No-op on a fresh/empty DB (no active system
    /// row) and skipped if the active prompt already mentions <c>browse_web</c> (idempotent), so a
    /// manual panel paste of the same guidance also suppresses it.
    /// </summary>
    public partial class SeedBrowserPromptGuidance : Migration
    {
        private const string Marker = "Append browse_web tool guidance (migration 20260610120000)";

        /// <summary>The guidance appended to the system prompt. Mirrors the browse_web tool's description.</summary>
        private const string BrowserBlock =
            "browse_web: a real web browser (Playwright). Use it for anything that needs a live page —\n" +
            "opening a site, reading rendered content, clicking/typing, or taking a screenshot. Don't use\n" +
            "consult_codex to load or screenshot pages; Codex has no browser. For \"screenshot <site>\" /\n" +
            "\"mach mir ein Screenshot von <site>\": call browse_web to open the site and take a full-page\n" +
            "screenshot — it saves the image to the media directory and returns the absolute file path —\n" +
            "then send that file to Phil with send_image.";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite string-literal escaping: double any single quotes in the block.
            var block = BrowserBlock.Replace("'", "''");
            // EF Core stores DateTimeOffset as TEXT in this exact shape; match it so it round-trips.
            var nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

            // 1. Insert a new active version = current active system prompt + a blank line + the block.
            //    Guards: an active system row must exist, and it must not already mention browse_web.
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
  AND Content NOT LIKE '%browse_web%';");

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
