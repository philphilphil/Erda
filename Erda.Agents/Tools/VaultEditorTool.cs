using System.ClientModel;
using System.ComponentModel;
using System.Text;
using Erda.Agents;                      // ToolCallActivity (namespace Erda.Agents, not .Tools)
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace Erda.Agents.Tools;

/// <summary>
/// The vault-editor sub-agent, exposed to Erda as a single tool (<c>edit_vault_note</c>). This is the
/// MAF agent-as-tool pattern specialized for convention-aware vault editing — the in-process
/// replacement for the retired codex <c>delegate_vault_task</c> capability.
///
/// Because the sub-agent's instructions depend on the target note's path (its hierarchical
/// <c>AGENTS.md</c> conventions), it is built <b>fresh per call</b> over a <see cref="ResponsesClient"/>
/// constructed once in the ctor (same endpoint/model as <see cref="ErdaAgent"/>). The sub-agent runs
/// its own isolated loop with a narrow vault tool set — read/search/edit/write + web search, and
/// <b>nothing else</b> (no reminder/notify/browser) — at hardcoded <b>high</b> reasoning effort, then
/// returns a brief chat summary. Erda never sees the intermediate tool calls.
/// </summary>
public sealed class VaultEditorTool
{
    private readonly VaultService _vault;
    private readonly ErdaOptions _options;
    private readonly ObservabilityOptions _observability;
    private readonly IActivityRecorder _recorder;
    private readonly ILogger<VaultEditorTool> _logger;
#pragma warning disable OPENAI001 // Responses surface is [Experimental].
    private readonly ResponsesClient _responses;   // built once, shared across every edit_vault_note call
#pragma warning restore OPENAI001

    public VaultEditorTool(
        VaultService vault,
        IOptions<ErdaOptions> options,
        IOptions<ObservabilityOptions> observability,
        IActivityRecorder recorder,
        ILogger<VaultEditorTool> logger)
    {
        _vault = vault;
        _options = options.Value;
        _observability = observability.Value;
        _recorder = recorder;
        _logger = logger;

        // Same recipe as ErdaAgent / ResponsesReasoner: the loopback proxy needs no real credential,
        // but the SDK still requires a non-empty string — blank ChatApiKey falls back to "local".
        var key = string.IsNullOrWhiteSpace(_options.ChatApiKey) ? "local" : _options.ChatApiKey;
#pragma warning disable OPENAI001 // Responses surface is [Experimental].
        _responses = new ResponsesClient(
            new ApiKeyCredential(key),
            new ResponsesClientOptions { Endpoint = new Uri(_options.ChatBaseUrl) });
#pragma warning restore OPENAI001
    }

    /// <summary>The single tool Erda sees. Hand-written (not <c>AsAIFunction</c>) so its bilingual
    /// trigger <see cref="DescriptionAttribute"/> rides on the method.</summary>
    public AITool AsTool() => AIFunctionFactory.Create(EditVaultNoteAsync, "edit_vault_note");

    /// <summary>Hardcoded reasoning effort for the sub-agent (intricate conventions; mirrors codex's
    /// always-high vault task — deliberately NOT the model-lowerable <c>ChatReasoningEffort</c> knob).</summary>
    internal const string SubAgentReasoningEffort = "high";

    /// <summary>The fixed CriticMarkup author name the sub-agent signs its marks with — pinned (not the
    /// model's per-run pick) so it stays consistent across runs, which the vault's AGENTS.md thread
    /// rules ("never reply to yourself", keyed on the author name) depend on.</summary>
    internal const string AuthorName = "Erda";

    /// <summary>Fixed directive prepended to the sub-agent's instructions, ahead of the note's stacked
    /// AGENTS.md, pinning the CriticMarkup author name to <see cref="AuthorName"/> and overriding the
    /// conventions' "choose an honest agent name" guidance.</summary>
    private const string AuthorPreamble =
        "[Erda operating directive — overrides any conflicting instruction below]\n" +
        "You are Erda's vault editor. Whenever you attribute a CriticMarkup mark, ALWAYS sign it with the " +
        "author name \"" + AuthorName + "\" — e.g. {author=\"" + AuthorName + "\">>…<<}. Use this exact name " +
        "every time; ignore any guidance below to \"choose\" your own agent name. Marks already signed with " +
        "other names (Codex/GPT/Claude/Gemini) are other reviewers — treat them per the thread rules.\n\n";

    /// <summary>The sub-agent's instructions: the fixed author-name directive followed by the note's
    /// stacked AGENTS.md conventions. Internal so a test can assert the pin is present and precedes the
    /// conventions.</summary>
    internal string BuildSubAgentInstructions(string notePath) => AuthorPreamble + _vault.StackConventions(notePath);

    /// <summary>The sub-agent's isolated tool set — read/search/edit/write + web search, and
    /// <b>nothing else</b> (no reminder/notify/browser). Exposed internally so the wiring tests can
    /// assert the contract without driving the live model loop.</summary>
    internal List<AITool> BuildSubAgentTools() => new()
    {
        AIFunctionFactory.Create(ReadNote,    "read_note"),
        AIFunctionFactory.Create(SearchNotes, "search_notes"),
        AIFunctionFactory.Create(EditNote,    "edit_note"),
        AIFunctionFactory.Create(WriteNote,   "write_note"),
        new HostedWebSearchTool(),
    };

    [Description(
        "Edit a NAMED existing note in the Obsidian vault, or capture a new note into the vault, " +
        "following the vault's own editing conventions. Use whenever Phil asks to review/check/" +
        "critique/proofread/edit/fix/rewrite/append a specific note, or to write a note down: " +
        "'review my draft', 'fix this note', 'rewrite ...', 'append ... to ...', 'capture this note', " +
        "'kritisiere ...', 'prüfe ...', 'Korrekturlesen', 'überarbeite ...', 'schreib ... in die Notiz'. " +
        "Resolve the fuzzy reference to a concrete vault-relative path first, then delegate. " +
        "A convention-aware sub-agent does the surgical editing and returns a brief summary.")]
    private async Task<string> EditVaultNoteAsync(
        [Description("Vault-relative path of the note, e.g. 'Efforts/On/Draft.md'.")] string path,
        [Description("What to do to the note, in Phil's own words/language.")] string instruction,
        [Description("Optional recent chat context to ground the edit.")] string? recentContext = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "No note path was provided. Resolve the request to a concrete vault-relative path first.";

        // 1. Per-call instructions: the fixed Erda author-name directive + the hierarchical conventions
        //    for THIS note's folder tree.
        string instructions;
        try
        {
            instructions = BuildSubAgentInstructions(path);
        }
        catch (Exception ex)
        {
            // A path escaping the root (or otherwise unresolvable) — relay rather than throw.
            return $"Cannot edit '{path}': {ex.Message}";
        }

        // 2. The sub-agent's isolated tool set — read/search/edit/write + web search, nothing else.
        var tools = BuildSubAgentTools();

        // 3. A FRESH sub-agent per call over the shared client, mirroring ErdaAgent's
        //    ChatClientAgentOptions build. Reasoning effort is HARDCODED high (the conventions are
        //    intricate) — NOT the model-lowerable ChatReasoningEffort knob.
#pragma warning disable OPENAI001 // ResponsesClient.AsAIAgent + Responses surface are [Experimental].
        AIAgent agent = _responses.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "vault-editor",
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools,
                RawRepresentationFactory = _ => new CreateResponseOptions
                {
                    ReasoningOptions = new ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = new ResponseReasoningEffortLevel(SubAgentReasoningEffort),
                    },
                },
            },
        }, _options.ChatModel);
#pragma warning restore OPENAI001

        // Same observability stack as ErdaAgent: OTel spans + the tool-call activity feed.
        agent = agent
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = _observability.CaptureMessageContent)
            .Use(ToolCallActivity.Middleware(_recorder, _logger))
            .Build();

        // 4. Streamed run (non-streamed Responses returns empty) → aggregate to final text.
        var message = new StringBuilder()
            .Append("Note: ").Append(path).Append("\n\n").Append(instruction);
        if (!string.IsNullOrWhiteSpace(recentContext))
            message.Append("\n\nRecent context:\n").Append(recentContext);

#pragma warning disable OPENAI001 // Responses surface is [Experimental].
        var response = await agent.RunStreamingAsync(message.ToString(), null, null, ct)
            .ToAgentResponseAsync(ct);
#pragma warning restore OPENAI001

        var text = (response.Text ?? string.Empty).Trim();
        return text.Length == 0 ? $"No changes were reported for {path}." : text;
    }

    // ---- sub-agent tool methods (each [Description]-annotated, returning a plain string) ----

    [Description("Read the full contents of a note from the vault.")]
    private string ReadNote(
        [Description("Vault-relative path, e.g. 'Efforts/On/Draft.md'.")] string path)
    {
        try { return _vault.ReadNote(path); }
        catch (Exception ex) { return $"Cannot read {path}: {ex.Message}"; }
    }

    [Description("Case-insensitive full-text search across all notes. Returns matching paths with a short snippet.")]
    private string SearchNotes(
        [Description("Text to search for.")] string query)
    {
        var hits = _vault.Search(query);
        if (hits.Count == 0)
            return $"No matches for '{query}'.";

        var sb = new StringBuilder();
        foreach (var (path, snippet) in hits)
            sb.AppendLine($"{path}: …{snippet}…");
        return sb.ToString().TrimEnd();
    }

    [Description(
        "Anchored, surgical edit: replace an exactly-once 'oldString' with 'newString' in a note. " +
        "Errors clearly if the anchor is absent or appears more than once (then add more surrounding " +
        "context to target a single location). Use this for review/critique/fix edits so surrounding " +
        "text is never touched.")]
    private string EditNote(
        [Description("Vault-relative path of the note.")] string path,
        [Description("Exact text to replace; must occur in the note exactly once.")] string oldString,
        [Description("Replacement text.")] string newString)
    {
        try
        {
            _vault.ReplaceInNote(path, oldString, newString);
            return $"Edited {path}.";
        }
        catch (Exception ex) { return $"Could not edit {path}: {ex.Message}"; }
    }

    [Description("Create a new note or overwrite an existing one with the given full content " +
                 "(use for writing-mode new notes; prefer edit_note for surgical changes).")]
    private string WriteNote(
        [Description("Vault-relative path, e.g. 'Inbox/New Idea.md'.")] string path,
        [Description("Full Markdown content to write.")] string content)
    {
        try
        {
            _vault.WriteNote(path, content);
            return $"Wrote {path} ({content.Length} chars).";
        }
        catch (Exception ex) { return $"Could not write {path}: {ex.Message}"; }
    }
}
