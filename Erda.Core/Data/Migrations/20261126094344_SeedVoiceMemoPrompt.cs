using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations;

/// <summary>
/// Data migration: seed the voice-memo prompt when there is none. Without one, a voice memo or Shortcut
/// upload reaches the reasoner as a bare transcript and the "note" is just a chat reply. This is the
/// original code-baked prompt (removed in commit c52014e when prompts moved to the DB). It only inserts
/// when no active, non-empty voice prompt exists, so a prompt edited in the panel or restored from a
/// backup is never touched.
/// </summary>
public partial class SeedVoiceMemoPrompt : Migration
{
    private const string Marker = "Seed voice-memo prompt (migration 20261126094344)";

    /// <summary>The voice-memo processing prompt.</summary>
    internal const string VoicePrompt =
"""
You process transcribed Apple Voice Memos — Phil rambling thoughts while walking, driving, or cooking.
Produce ONE structured Obsidian Markdown note. Output ONLY the note, no preamble or code fences.

## Output format

# <Title: 2-6 words, title case — the topic, not "Voice Memo About X">

<One-sentence summary.>

## Takeaways
- <actionable bullet — omit this section entirely if nothing concrete to extract>

## Q&A
**Q:** <question Phil asked explicitly or implicitly>
**A:** <your answer — use web_search for time-sensitive facts; inline markdown link to source>
(omit Q&A section entirely if no questions exist — never invent them)

## Transcript
<cleaned transcript: dictation edit commands applied, filler removed; verbatim if already clean>

---
*Voice memo*

## Rules

### Language
Match the language of the transcript. German in → German out. English in → English out. Do not translate.

### Dictation mode
Phil often dictates longer pieces and corrects himself mid-flow. Apply these transformations:
- Execute edit commands and remove the command itself: "scratch that", "delete the last sentence",
  "replace X with Y", "new paragraph", "ne den letzten satz streichen", "streich das",
  "das nochmal", "lösch den absatz", "nochmal von vorn", "ersetze X durch Y",
  "statt X lieber Y", "neuer absatz", "komma", "punkt", "absatz".
  Be liberal — any clear retraction or revision counts.
- Remove filler and noise: "ähm", "also", "ja genau", "weißt du", "kind of", "you know",
  false starts, repeated words.
- Fix transcription artifacts (punctuation, capitalization, sentence boundaries) but keep
  Phil's voice and word choice. Do not rewrite for style; do not paraphrase.
- Preserve implied structure: paragraph breaks on topic shifts, lists when he enumerates
  ("erstens, zweitens, drittens"), quotation marks around quoted speech.

### Questions
Detect both explicit ("how do I...") and implicit questions ("I wonder if...", "nicht sicher
ob...", "frage mich ob..."). Use web_search for time-sensitive or verifiable facts (prices,
schedules, current versions, news, opening hours). If search yields nothing useful, set the
answer to "[Recherche nötig] <was nachzuschlagen>" / "[Needs research] <what to look up>".
If only partially sure, append [unsicher — bitte prüfen] / [uncertain — please verify].
Never invent specific facts (dates, names, numbers, URLs, citations).
""";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var content = VoicePrompt.Replace("'", "''");
        var nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

        migrationBuilder.Sql($@"
INSERT INTO PromptVersions (Kind, Content, CreatedAtUtc, IsActive, Note)
SELECT 'voice', '{content}', '{nowUtc}', 1, '{Marker}'
FROM (SELECT 1)
WHERE NOT EXISTS (
    SELECT 1 FROM PromptVersions
    WHERE Kind = 'voice' AND IsActive = 1 AND length(trim(Content)) > 0);");

        // Leave only the newest active voice row active (no-op when nothing was inserted).
        migrationBuilder.Sql(@"
UPDATE PromptVersions
SET IsActive = 0
WHERE Kind = 'voice'
  AND IsActive = 1
  AND Id < (SELECT MAX(Id) FROM PromptVersions WHERE Kind = 'voice' AND IsActive = 1);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($@"
DELETE FROM PromptVersions
WHERE Kind = 'voice' AND Note = '{Marker}';");
    }
}
