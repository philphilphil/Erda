namespace Erda.Core.Services;

/// <summary>
/// The one Codex entry point the reminder scheduler needs, behind an interface so the Codex-direct
/// dispatch branch can be unit-tested with a fake instead of shelling out to the <c>codex</c> CLI.
/// <see cref="CodexRunner"/> implements it; other consumers keep using the concrete type.
/// </summary>
public interface ICodexRunner
{
    /// <summary>
    /// Runs <c>codex exec</c> on an already-built prompt and returns Codex's final message.
    /// <paramref name="enableWebSearch"/> turns on Codex's native web_search tool.
    /// </summary>
    Task<string> RunPromptAsync(
        string prompt, bool enableWebSearch = false, CancellationToken cancellationToken = default,
        string? logLabel = null, string? reasoningEffort = null);
}
