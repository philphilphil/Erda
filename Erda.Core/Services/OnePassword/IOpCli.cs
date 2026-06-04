namespace Erda.Core.Services.OnePassword;

/// <summary>
/// The single seam to the 1Password <c>op</c> CLI. Runs <c>op</c> with a verbatim argument list
/// (no shell) and returns its stdout, throwing <see cref="OpCliException"/> on a non-zero exit.
/// Everything that interprets <c>op</c> output (the secret resolver, the login lookup, the accounts
/// panel) depends on this interface so it can be unit-tested with a fake — only the real
/// <see cref="OpCli"/> needs the binary.
/// </summary>
public interface IOpCli
{
    /// <summary>Run <c>op</c> with <paramref name="args"/>; returns trimmed stdout. Throws
    /// <see cref="OpCliException"/> if the process exits non-zero or cannot be launched.</summary>
    Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when an <c>op</c> invocation fails. The message carries the exit code and a
/// trimmed tail of stderr — never a resolved secret value.</summary>
public sealed class OpCliException(string message) : Exception(message);
