using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class CodexRunnerTests
{
    [Theory]
    [InlineData("low", "low")]
    [InlineData("High", "high")]    // case-insensitive, normalized to lower
    [InlineData("MEDIUM", "medium")]
    [InlineData("minimal", "minimal")]
    [InlineData(" high ", "high")]  // trimmed
    public void Accepts_known_efforts(string requested, string expected)
        => Assert.Equal(expected, CodexRunner.NormalizeReasoningEffort(requested, "high"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("turbo")]   // unknown value
    public void Falls_back_to_default_for_missing_or_unknown(string? requested)
        => Assert.Equal("high", CodexRunner.NormalizeReasoningEffort(requested, "high"));

    [Fact]
    public async Task Closes_child_stdin_so_a_stdin_reading_codex_completes()
    {
        // A fake codex that reads stdin to EOF before doing its work. If RunPromptAsync did not
        // close the child's stdin, this read would block forever and the run would hit the timeout.
        var script = WriteFakeCodex("cat >/dev/null");
        var runner = new CodexRunner(
            Options.Create(new ErdaOptions { CodexExecutable = script, CodexTimeout = TimeSpan.FromSeconds(10) }),
            NullLogger<CodexRunner>.Instance);

        var result = await runner.RunPromptAsync("hello");

        Assert.Equal("FAKE_OK", result);
    }

    [Fact]
    public async Task Times_out_and_kills_a_hanging_codex()
    {
        var script = WriteFakeCodex("sleep 30"); // ignores stdin; just hangs past the timeout
        var runner = new CodexRunner(
            Options.Create(new ErdaOptions { CodexExecutable = script, CodexTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<CodexRunner>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunPromptAsync("hello"));
    }

    [Theory]
    [InlineData("ERROR: token_invalidated. Please sign in again.")]
    [InlineData("Your refresh token has already been used (refresh_token_reused).")]
    [InlineData("Your access token could not be refreshed because your refresh token was already used.")]
    [InlineData("invalid_grant: The specified refresh token is no longer valid.")]
    public async Task Auth_failure_surfaces_a_clean_login_message(string stderrLine)
    {
        var script = WriteFailingCodex(stderrLine, exitCode: 1);
        var runner = new CodexRunner(
            Options.Create(new ErdaOptions { CodexExecutable = script, CodexTimeout = TimeSpan.FromSeconds(10) }),
            NullLogger<CodexRunner>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunPromptAsync("hi"));

        Assert.Contains("codex login", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The raw 401/token stderr is logged, not dumped into the user-facing message.
        Assert.DoesNotContain("refresh_token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token_invalidated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_auth_failure_still_reports_exit_code_and_stderr()
    {
        var script = WriteFailingCodex("model gpt-5.5 not found", exitCode: 2);
        var runner = new CodexRunner(
            Options.Create(new ErdaOptions { CodexExecutable = script, CodexTimeout = TimeSpan.FromSeconds(10) }),
            NullLogger<CodexRunner>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunPromptAsync("hi"));

        Assert.Contains("exit 2", ex.Message);
        Assert.Contains("not found", ex.Message);
        Assert.DoesNotContain("codex login", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Write an executable fake-codex shell script: run <paramref name="preamble"/>, then write
    /// "FAKE_OK" to the file named by the <c>-o</c> argument and exit 0.</summary>
    private static string WriteFakeCodex(string preamble)
    {
        var path = Path.Combine(Path.GetTempPath(), "fake-codex-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path,
            "#!/bin/bash\n" +
            preamble + "\n" +
            "out=\"\"; prev=\"\"\n" +
            "for a in \"$@\"; do [ \"$prev\" = \"-o\" ] && out=\"$a\"; prev=\"$a\"; done\n" +
            "[ -n \"$out\" ] && printf 'FAKE_OK' > \"$out\"\n" +
            "exit 0\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Write an executable fake-codex that drains stdin, prints <paramref name="stderrLine"/>
    /// to stderr, and exits with <paramref name="exitCode"/> (no <c>-o</c> output file written).</summary>
    private static string WriteFailingCodex(string stderrLine, int exitCode)
    {
        if (stderrLine.Contains('\'')) throw new ArgumentException("Test stderr must not contain single quotes.");
        var path = Path.Combine(Path.GetTempPath(), "fail-codex-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path,
            "#!/bin/bash\n" +
            "cat >/dev/null\n" +                 // drain stdin to EOF so the runner's stdin-close path is exercised
            $"echo '{stderrLine}' >&2\n" +
            $"exit {exitCode}\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
