namespace Erda.Core.Services;

/// <summary>
/// Runs a user-authored shell command before a scheduled prompt fires, returning its stdout for
/// injection into the prompt. Behind an interface so the scheduler's pre-script step is unit-testable
/// with a fake instead of launching a real subprocess.
/// </summary>
public interface IPreScriptRunner
{
    /// <summary>
    /// Run a shell command and return its stdout (trimmed, capped). Throws on non-zero exit, launch
    /// failure, or timeout.
    /// </summary>
    Task<string> RunAsync(string script, CancellationToken cancellationToken = default);
}
