using Erda.Core.Services;
using Erda.Core.Abstractions;
using Erda.Core.Data;

namespace Erda.Agents.Workflows;

/// <summary>
/// Processes a text transcript (already transcribed) as a Voice Memo:
/// the reasoner with the voice-memo prompt → write to "1 Inbox/" in the vault.
/// Used by the WhatsApp channel when an Apple Voice Memo (.m4a) is shared, so the audio is
/// transcribed once and the memo pipeline runs on the text — no double-transcription.
/// The voice-memo prompt is read from the store (authored in the control panel); empty when none has
/// been saved yet (fresh DB).
/// </summary>
public sealed class MemoProcessor(
    IReasoner reasoner,
    VaultService vault,
    IPromptStore prompts,
    ILogger<MemoProcessor> logger) : IMemoProcessor
{
    private const string InboxFolder = "1 Inbox";

    public async Task<MemoResult> ProcessAsync(string transcript, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("MemoProcessor: processing {Chars}-char transcript.", transcript.Length);
        var instruction = prompts.GetActiveContent(PromptKind.Voice) ?? "";
        var note = await reasoner.RunAsync(instruction, transcript, cancellationToken);
        var relative = WriteToInbox(note);
        logger.LogInformation("MemoProcessor: saved {Chars} chars to {Path}.", note.Length, relative);
        return new MemoResult($"Saved voice memo to {relative} ({note.Length} chars).", relative);
    }

    public Task<string> SaveRawAsync(string transcript, CancellationToken cancellationToken = default)
    {
        // Seconds precision (not the HHmm the formatted path uses): raw saves are a failure fallback and
        // may retry within the same minute, and WriteNote overwrites — so avoid clobbering.
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss");
        var relative = $"{InboxFolder}/{stamp}_voice-memo-raw.md";
        var body =
            "# Voice memo (raw transcript)\n\n" +
            "> ⚠️ Automatic formatting failed (model unavailable); this is the unprocessed transcript.\n\n" +
            transcript + "\n";
        vault.WriteNote(relative, body);
        logger.LogWarning("MemoProcessor: saved RAW transcript ({Chars} chars) to {Path} after a formatting failure.",
            transcript.Length, relative);
        return Task.FromResult(relative);
    }

    private string WriteToInbox(string note)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmm");
        var slug = ExtractSlug(note);
        var relative = $"{InboxFolder}/{stamp}_{slug}.md";
        vault.WriteNote(relative, note);
        return relative;
    }

    private static string ExtractSlug(string note)
    {
        foreach (var line in note.Split('\n'))
        {
            if (line.StartsWith("# "))
            {
                var title = line[2..].Trim();
                if (title.Length > 0)
                    return Slugify(title, maxWords: 4);
            }
        }
        return "voice-memo";
    }

    private static string Slugify(string title, int maxWords)
    {
        var normalized = title
            .Replace("ä", "ae").Replace("Ä", "ae")
            .Replace("ö", "oe").Replace("Ö", "oe")
            .Replace("ü", "ue").Replace("Ü", "ue")
            .Replace("ß", "ss")
            .ToLowerInvariant();
        var words = normalized
            .Split([' ', '-', '_', ',', '.', '!', '?', ':', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Take(maxWords)
            .Select(w => System.Text.RegularExpressions.Regex.Replace(w, "[^a-z0-9]", ""))
            .Where(w => w.Length > 0);
        var slug = string.Join("-", words);
        return slug.Length > 0 ? slug : "voice-memo";
    }
}
