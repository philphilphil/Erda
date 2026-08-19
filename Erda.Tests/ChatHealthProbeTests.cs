using Erda.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// The probe runs the real reasoning path, so every way the proxy can misbehave — refusing the
/// connection, hanging, answering with nothing — has to come back as a failed result rather than an
/// exception, or the watch loop would log an error instead of alerting.
/// </summary>
public class ChatHealthProbeTests
{
    private static ReasonerChatHealthProbe Probe(FakeReasoner reasoner) =>
        new(reasoner, NullLogger<ReasonerChatHealthProbe>.Instance);

    [Fact]
    public async Task A_normal_answer_is_healthy_and_asks_for_no_web_search()
    {
        var reasoner = new FakeReasoner { Result = "OK" };

        var result = await Probe(reasoner).ProbeAsync(TimeSpan.FromMinutes(1));

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        var call = Assert.Single(reasoner.Calls);
        Assert.False(call.WebSearch);
        Assert.Equal("low", call.ReasoningEffort);
        Assert.Equal(ReasonerChatHealthProbe.Prompt, call.Prompt);
    }

    [Fact]
    public async Task An_empty_answer_counts_as_down()
    {
        var result = await Probe(new FakeReasoner { Result = "   " }).ProbeAsync(TimeSpan.FromMinutes(1));

        Assert.False(result.Ok);
        Assert.Contains("no content", result.Error);
    }

    [Fact]
    public async Task A_throwing_endpoint_counts_as_down_and_keeps_the_reason()
    {
        var reasoner = new FakeReasoner { Throw = new HttpRequestException("Connection refused (127.0.0.1:10531)") };

        var result = await Probe(reasoner).ProbeAsync(TimeSpan.FromMinutes(1));

        Assert.False(result.Ok);
        Assert.Contains("HttpRequestException", result.Error);
        Assert.Contains("Connection refused", result.Error);
    }

    [Fact]
    public async Task A_hanging_endpoint_times_out_into_a_failed_result()
    {
        var result = await new ReasonerChatHealthProbe(new HangingReasoner(), NullLogger<ReasonerChatHealthProbe>.Instance)
            .ProbeAsync(TimeSpan.FromMilliseconds(50));

        Assert.False(result.Ok);
        Assert.Contains("no answer within", result.Error);
    }

    [Fact]
    public async Task Host_shutdown_propagates_instead_of_looking_like_an_outage()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReasonerChatHealthProbe(new HangingReasoner(), NullLogger<ReasonerChatHealthProbe>.Instance)
                .ProbeAsync(TimeSpan.FromMinutes(1), cts.Token));
    }

    [Fact]
    public void Summarize_flattens_and_truncates_a_long_message()
    {
        var summary = ReasonerChatHealthProbe.Summarize(new InvalidOperationException("a\nb" + new string('x', 500)));

        Assert.StartsWith("InvalidOperationException: a b", summary);
        Assert.EndsWith("…", summary);
        Assert.True(summary.Length < 400);
    }

    /// <summary>A reasoner that never answers, to exercise the probe's own deadline.</summary>
    private sealed class HangingReasoner : Erda.Core.Services.IReasoner
    {
        public async Task<string> ReasonAsync(string prompt, bool webSearch = false,
            CancellationToken cancellationToken = default, string? logLabel = null, string? reasoningEffort = null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "";
        }

        public Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken cancellationToken = default)
            => ReasonAsync(developerInstruction, cancellationToken: cancellationToken);
    }
}
