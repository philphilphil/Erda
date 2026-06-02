namespace Erda.Core.Abstractions;

/// <summary>Processes an already-transcribed text as a Voice Memo (Codex → "1 Inbox/").</summary>
public interface IMemoProcessor
{
    Task<string> ProcessAsync(string transcript, CancellationToken cancellationToken = default);
}
