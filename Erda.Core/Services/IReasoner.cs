namespace Erda.Core.Services;

/// <summary>
/// The in-process reasoning seam that replaces the old <c>codex</c> subprocess (<c>ICodexRunner</c>).
/// Backed by the streamed OpenAI Responses surface (<c>ResponsesReasoner</c>, in the MAF layer): same
/// shape as the former Codex oracle — <c>prompt (+ optional web search) → final text</c> — but an HTTP
/// call to the local OpenAI-compatible endpoint instead of shelling out. Every former Codex consumer
/// (voice-memo, recipe, error-watch) takes this.
/// </summary>
public interface IReasoner
{
    /// <summary>
    /// Runs the model on an already-built prompt and returns its final message. <paramref name="webSearch"/>
    /// attaches the hosted <c>web_search</c> tool so the model can ground its answer in current sources.
    /// <paramref name="reasoningEffort"/> is normalized against <see cref="Configuration.ErdaOptions.ChatReasoningEffort"/>
    /// (null/invalid ⇒ the configured default; valid levels are low/medium/high).
    /// </summary>
    Task<string> ReasonAsync(
        string prompt, bool webSearch = false, CancellationToken ct = default,
        string? logLabel = null, string? reasoningEffort = null);

    /// <summary>
    /// Voice-memo convenience: builds the prompt from a developer instruction + transcript, then reasons
    /// with web search ON. Preserves the old <c>CodexRunner.RunAsync</c> semantics (the voice-memo
    /// workflow's executor depends on this shape).
    /// </summary>
    Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken ct = default);
}
