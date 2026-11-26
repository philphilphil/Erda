namespace Erda.Core.Services;

/// <summary>
/// The in-process reasoning seam: <c>prompt (+ optional web search) → final text</c>, a single
/// stateless call to the chat model. Implemented in the MAF layer (<c>ChatReasoner</c>); used by
/// voice-memo processing, recipe import and error-watch analysis.
/// </summary>
public interface IReasoner
{
    /// <summary>
    /// Runs the model on an already-built prompt and returns its final message. <paramref name="webSearch"/>
    /// attaches the hosted web-search tool so the model can ground its answer in current sources. The
    /// reasoning effort is the configured <see cref="Configuration.ErdaOptions.ChatReasoningEffort"/>.
    /// </summary>
    Task<string> ReasonAsync(string prompt, bool webSearch = false, CancellationToken ct = default, string? logLabel = null);

    /// <summary>
    /// Voice-memo convenience: builds the prompt from a developer instruction + transcript, then reasons
    /// with web search ON (the voice-memo workflow's executor depends on this shape).
    /// </summary>
    Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken ct = default);
}
