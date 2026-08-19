using System.Diagnostics;
using Erda.Core.Services;

namespace Erda.Core.Scheduling;

/// <summary>Outcome of one probe: whether the chat endpoint answered, and if not, why.</summary>
/// <param name="Ok">True when the endpoint returned a usable answer.</param>
/// <param name="Error">Short human-readable failure reason; null when <paramref name="Ok"/>.</param>
/// <param name="Elapsed">Wall-clock time the probe took (answered or failed).</param>
public sealed record ChatProbeResult(bool Ok, string? Error, TimeSpan Elapsed);

/// <summary>Sends one trivial request through the chat endpoint to see whether it is alive.</summary>
public interface IChatHealthProbe
{
    /// <summary>
    /// Probes the endpoint, giving up after <paramref name="timeout"/>. Never throws for an endpoint
    /// failure — a broken endpoint is a <see cref="ChatProbeResult"/> with <c>Ok=false</c>. Only host
    /// shutdown (a cancelled <paramref name="ct"/>) propagates.
    /// </summary>
    Task<ChatProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Probes the local OpenAI-compatible proxy through <see cref="IReasoner"/> — deliberately the same
/// path the agent and every reasoning consumer take (streamed Responses API against
/// <c>Erda:ChatBaseUrl</c> with <c>Erda:ChatModel</c>), so a probe that passes means real work would
/// too. A raw HTTP ping would miss exactly the failures we care about: the proxy up but logged out,
/// or answering with an empty <c>output</c>.
///
/// The prompt is one line and reasoning effort is pinned to <c>low</c> — this runs hourly and only
/// needs a round trip, not thinking.
/// </summary>
public sealed class ReasonerChatHealthProbe(IReasoner reasoner, ILogger<ReasonerChatHealthProbe> logger) : IChatHealthProbe
{
    /// <summary>The probe prompt: cheap, deterministic, and obviously not a real request in the logs.</summary>
    public const string Prompt = "Health check. Reply with exactly: OK";

    private const int MaxErrorChars = 300;

    public async Task<ChatProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
            cts.CancelAfter(timeout);

        try
        {
            var text = await reasoner.ReasonAsync(
                Prompt, webSearch: false, cts.Token, logLabel: "chat-health probe", reasoningEffort: "low");

            return string.IsNullOrWhiteSpace(text)
                ? new ChatProbeResult(false, "the endpoint answered with no content", sw.Elapsed)
                : new ChatProbeResult(true, null, sw.Elapsed);
        }
        // Host shutdown — not an outage, and the catch-all below would otherwise swallow it.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // Our own deadline fired (the host is not shutting down) — that's a failed probe, not a cancel.
        catch (OperationCanceledException)
        {
            logger.LogWarning("Chat-health probe timed out after {Timeout}.", timeout);
            return new ChatProbeResult(false, $"no answer within {Describe(timeout)}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-health probe failed after {ElapsedMs}ms.", sw.ElapsedMilliseconds);
            return new ChatProbeResult(false, Summarize(ex), sw.Elapsed);
        }
    }

    /// <summary>One line naming the exception type and its (truncated) message, for the alert text.</summary>
    public static string Summarize(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > MaxErrorChars)
            message = message[..MaxErrorChars] + "…";
        return $"{ex.GetType().Name}: {message}";
    }

    private static string Describe(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{(int)t.TotalSeconds}s" : $"{(int)t.TotalMinutes}m";
}
