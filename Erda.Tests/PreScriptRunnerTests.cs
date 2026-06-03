using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for <see cref="PreScriptRunner"/> against a real <c>/bin/sh</c> subprocess (deterministic and
/// safe — short commands). Exercises stdout capture, the output cap, non-zero exit, and the timeout.
/// </summary>
public class PreScriptRunnerTests
{
    private static PreScriptRunner Make(int maxChars = 8000, int timeoutMs = 30000) =>
        new(Options.Create(new ReminderOptions
        {
            PreScriptMaxOutputChars = maxChars,
            PreScriptTimeout = TimeSpan.FromMilliseconds(timeoutMs),
        }), NullLogger<PreScriptRunner>.Instance);

    [Fact]
    public async Task Returns_trimmed_stdout()
    {
        Assert.Equal("hello", await Make().RunAsync("echo hello"));
    }

    [Fact]
    public async Task Truncates_output_beyond_the_cap()
    {
        var result = await Make(maxChars: 5).RunAsync("echo abcdefghij");
        Assert.StartsWith("abcde", result);
        Assert.Contains("context truncated", result);
    }

    [Fact]
    public async Task Nonzero_exit_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Make().RunAsync("exit 3"));
    }

    [Fact]
    public async Task Timeout_kills_the_process_and_throws()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => Make(timeoutMs: 200).RunAsync("sleep 5"));
    }
}
