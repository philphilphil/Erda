using System.ComponentModel;
using Erda.Services;
using Microsoft.Extensions.AI;

namespace Erda.Tools;

/// <summary>
/// Exposes Codex (gpt-5.5, high reasoning, on the ChatGPT subscription) WITH web search as a
/// single delegation tool: consult_codex.
///
/// Erda runs on a small/fast model (gpt-5-mini) whose knowledge is limited and can be stale, so
/// it delegates two kinds of work here: (1) factual/current questions — Codex grounds the answer
/// with live web search and cites sources; (2) genuinely hard reasoning. Codex is a stateless
/// oracle (no memory between calls, no access to Erda's vault), so the orchestrator must pass any
/// needed context in the call. Returns Markdown text.
/// </summary>
public sealed class ReasoningTools(CodexRunner codex)
{
    private const string DeveloperInstruction =
        "You are a research-and-reasoning oracle answering for an orchestrator agent. Use web " +
        "search to ground anything factual, recent, or niche — do NOT answer factual questions " +
        "from memory alone. Think carefully, then reply with the answer as Markdown text only. " +
        "When you used sources, end with a short '## Sources' list of URLs. Do not create or edit " +
        "files and do not run shell commands — just return the answer.";

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ConsultCodex, "consult_codex"),
    ];

    [Description(
        "Consult a stronger model (Codex, gpt-5.5, high effort) WITH live web search. Use this " +
        "whenever the answer depends on facts about a topic, technology, product, person, or event " +
        "— especially anything recent or niche where your own knowledge may be wrong or stale — and " +
        "for genuinely hard analysis, planning, or math. It grounds answers in current sources and " +
        "cites them. The model cannot see the vault and has no memory between calls, so include any " +
        "needed context in 'context'. Returns Markdown (with a Sources list when web search was used). " +
        "Note: takes ~10-30s.")]
    private async Task<string> ConsultCodex(
        [Description("The question or task, stated clearly and self-contained.")] string question,
        [Description("Optional supporting context (e.g. note contents you already fetched) to reason over.")] string? context = null)
    {
        var prompt = string.IsNullOrWhiteSpace(context)
            ? $"{DeveloperInstruction}\n\nQuestion:\n{question}"
            : $"{DeveloperInstruction}\n\nQuestion:\n{question}\n\nContext:\n{context}";

        return await codex.RunPromptAsync(prompt, enableWebSearch: true, logLabel: question);
    }
}
