namespace Erda.Core.Abstractions;

/// <summary>Outcome of processing a voice memo: the user-facing reply plus the vault-relative note path.</summary>
public sealed record MemoResult(string Reply, string NotePath);

/// <summary>Processes an already-transcribed text as a Voice Memo (Codex → "1 Inbox/").</summary>
public interface IMemoProcessor
{
    Task<MemoResult> ProcessAsync(string transcript, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a raw, unformatted transcript to "1 Inbox/" as a fallback when <see cref="ProcessAsync"/>
    /// can't run (e.g. the reasoner is overloaded/unavailable). Guarantees the memo's content survives
    /// even if formatting fails. Returns the vault-relative path written.
    /// </summary>
    Task<string> SaveRawAsync(string transcript, CancellationToken cancellationToken = default);
}
