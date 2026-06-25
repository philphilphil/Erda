using System.ComponentModel;
using Erda.Core.Services;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// Exposes Codex (gpt-5.5 on the ChatGPT subscription, per-call reasoning effort) WITH web search
/// as two delegation tools:
/// <list type="bullet">
/// <item><c>consult_codex</c> — a stateless oracle (no memory, no vault access); the orchestrator
/// passes any needed context in the call.</item>
/// <item><c>delegate_vault_task</c> — runs codex directly inside Phil's Obsidian vault, where it can
/// read, search, create, and edit notes with its own shell. No need to pre-fetch note contents.</item>
/// </list>
///
/// Erda runs on a small/fast model (gpt-5-mini) whose knowledge is limited and can be stale, so it
/// delegates here: (1) factual/current questions — Codex grounds the answer with live web search and
/// cites sources; (2) genuinely hard reasoning; (3) multi-note vault work. Returns Markdown text.
/// </summary>
public sealed class ReasoningTools(CodexRunner codex)
{
    private const string DeveloperInstruction =
        "You are a research-and-reasoning oracle answering for an orchestrator agent. Use web " +
        "search to ground anything factual, recent, or niche — do NOT answer factual questions " +
        "from memory alone. Think carefully, then reply with the answer as Markdown text only. " +
        "When you used sources, end with a short '## Sources' list of URLs. Do not create or edit " +
        "files and do not run shell commands — just return the answer.";

    private const string VaultDeveloperInstruction =
        "You are working directly inside Phil's Obsidian vault — a tree of Markdown notes — which is " +
        "your current working directory. FIRST read the `AGENTS.md` at the vault root and follow it: " +
        "it is the authoritative guide for working in this vault (review vs. writing mode, CriticMarkup " +
        "conventions, where files may be created, how to search). Also honor any nearer `AGENTS.md` in " +
        "a note's own folder — the nearer file wins on conflicts. Use your shell tools (rg/grep/cat) to " +
        "read and search notes, and do not touch files outside the vault. When done, reply with a " +
        "concise Markdown summary of exactly what you read and what you changed, listing the file paths.";

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ConsultCodex, "consult_codex"),
        AIFunctionFactory.Create(DelegateVaultTask, "delegate_vault_task"),
    ];

    [Description(
        "Consult a stronger model (Codex, gpt-5.5) WITH live web search. Use this " +
        "whenever the answer depends on facts about a topic, technology, product, person, or event " +
        "— especially anything recent or niche where your own knowledge may be wrong or stale — and " +
        "for genuinely hard analysis, planning, or math. It grounds answers in current sources and " +
        "cites them. The model cannot see the vault and has no memory between calls, so include any " +
        "needed context in 'context'. Returns Markdown (with a Sources list when web search was used).")]
    private async Task<string> ConsultCodex(
        [Description("The question or task, stated clearly and self-contained.")] string question,
        [Description("Optional supporting context (e.g. note contents you already fetched) to reason over.")] string? context = null,
        [Description("Reasoning depth: 'low' for quick factual/current lookups (weather, prices, definitions, news, " +
                     "\"what is X\") — fast, ~10s. 'high' for genuinely hard analysis, planning, math, or code — slow, " +
                     "30s+. 'medium' in between. Default 'low'; only raise it when the task is actually hard.")] string effort = "low")
    {
        var prompt = string.IsNullOrWhiteSpace(context)
            ? $"{DeveloperInstruction}\n\nQuestion:\n{question}"
            : $"{DeveloperInstruction}\n\nQuestion:\n{question}\n\nContext:\n{context}";

        return await codex.RunPromptAsync(prompt, enableWebSearch: true, logLabel: question, reasoningEffort: effort);
    }

    [Description(
        "Delegate a task that operates on Phil's Obsidian vault to a stronger model (Codex, gpt-5.5) " +
        "that has DIRECT read/write access to the vault. Codex reads, searches, creates, and edits " +
        "notes itself with its own shell — so do NOT pre-fetch note contents; just describe the task. " +
        "Use this for multi-note review, cleanup, refactoring, or any vault work that benefits from " +
        "stronger reasoning over many files. For a simple single-note read or write, use the vault " +
        "tools directly instead; for world-knowledge questions with no vault, use consult_codex. " +
        "Returns a Markdown summary of what Codex read and changed.")]
    private async Task<string> DelegateVaultTask(
        [Description("The vault task, stated clearly and self-contained (e.g. 'review notes in Projects/ for stale TODOs and tidy them').")] string task)
    {
        var prompt = $"{VaultDeveloperInstruction}\n\nTask:\n{task}";
        // Vault work always runs at the highest reasoning effort — quality over speed.
        return await codex.RunVaultTaskAsync(prompt, logLabel: task, reasoningEffort: "high");
    }
}
