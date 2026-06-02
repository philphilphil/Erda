using Erda.Configuration;
using Erda.Services;
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
}
