using Erda.Core.Services;
using Erda.Core.Abstractions;
using Erda.Core.Data;

namespace Erda.Agents.Workflows;

/// <summary>
/// Processes a text transcript (already transcribed) as a Voice Memo:
/// Codex with the voice-memo prompt → write to "1 Inbox/" in the vault.
/// Used by the WhatsApp channel when an Apple Voice Memo (.m4a) is shared, so the audio is
/// transcribed once and the memo pipeline runs on the text — no double-transcription.
/// The voice-memo prompt is read from the store (editable in the control panel), with
/// <see cref="VoiceMemoWorkflow.DeveloperInstruction"/> as the code-baked seed/default.
/// </summary>
public sealed class MemoProcessor(
    CodexRunner codex,
    VaultService vault,
    IPromptStore prompts,
    ILogger<MemoProcessor> logger) : IMemoProcessor
{
    private const string InboxFolder = "1 Inbox";

    public async Task<string> ProcessAsync(string transcript, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("MemoProcessor: processing {Chars}-char transcript.", transcript.Length);
        var instruction = prompts.GetActiveContent(PromptKind.Voice, VoiceMemoWorkflow.DeveloperInstruction);
        var note = await codex.RunAsync(instruction, transcript, cancellationToken);
        var relative = WriteToInbox(note);
        logger.LogInformation("MemoProcessor: saved {Chars} chars to {Path}.", note.Length, relative);
        return $"Saved voice memo to {relative} ({note.Length} chars).";
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
