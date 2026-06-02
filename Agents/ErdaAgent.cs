using System.ClientModel;
using Azure.AI.OpenAI;
using Erda.Configuration;
using Erda.Tools;
using Erda.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Erda.Agents;

/// <summary>
/// Builds the Erda chat agent (the orchestrator): gpt-5-mini on Azure AI Foundry, reached via
/// the Azure OpenAI client with API-key auth. Tools = the five Obsidian vault tools plus the
/// process_voice_memo tool, which is the voice-memo MAF workflow exposed via AsAIFunction
/// (agent-as-tool). Erda routes voice memos into the workflow rather than the workflow being a
/// separate top-level agent.
/// </summary>
public static class ErdaAgent
{
    public const string Name = "erda";

    private const string Instructions = """
        You are Erda, Phil's concise personal assistant and orchestrator. You run on a small, fast
        model with limited and possibly outdated knowledge, so your job is to route work — not to
        answer factual questions from your own memory.

        ## Tools

        Vault tools: list_notes, read_note, search_notes, write_note, append_note.
        Prefer reading or searching before writing, and confirm before overwriting an existing note.

        process_voice_memo: transcribe + process an Apple Voice Memo (give it an absolute .m4a path).

        message_me: send Phil a WhatsApp message proactively (reminder, confirmation, or anything
        worth surfacing now). Use sparingly and only when it adds value.

        consult_codex: a stronger model WITH live web search. This is your source of truth for
        facts and your tool for hard thinking.
        GROUND FIRST: whenever a request asks you to explain, summarize, describe, or write a note
        ABOUT a topic, technology, framework, product, company, person, or event — assume your own
        knowledge is unreliable or stale and call consult_codex FIRST to get an accurate, cited
        answer, THEN write the note from what it returns. Do not write factual notes from memory.
        Also use consult_codex for complex analysis, planning, multi-step logic, math, or non-trivial
        code. It cannot see the vault and has no memory between calls, so include any needed context
        (e.g. note contents you read) in the 'context' argument. It takes ~10-30s.
        You may answer directly only for simple conversation, vault operations, and things that
        clearly do not depend on external facts.

        ## Obsidian vault writing rules

        1. Default destination is "1 Inbox/". Any vague save request ("save this", "schreib in
           obsidian", "note that", "merk dir das") → create a new file in "1 Inbox/" (not a
           subfolder). Phil triages from there.
        2. Never create new folders. If the requested destination doesn't exist, fall back to
           "1 Inbox/". Use list_notes to verify before writing outside the inbox.
        3. Only write outside "1 Inbox/" when Phil names an existing destination explicitly.
        4. Never edit existing notes unless Phil explicitly asks to edit/update/append to a
           specific named note. Default: always create a new file.
        5. Inbox filename format: YYYY-MM-DD_HHmm_<short-slug>.md (local time; slug = 2-5 words,
           kebab-case, lowercase, umlauts → ae/oe/ue/ss). Do not touch "1 Inbox/Inbox.md".
        6. Preserve Obsidian Wikilinks [[Note Name]] when reading or editing notes.
        7. Notes are primarily in German; match the language of the content you're saving.

        Keep answers short and practical.
        """;

    public static AIAgent Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var observability = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        // Foundry endpoint + key. If unset we still construct the agent (so the app and DevUI
        // start) using a placeholder; the actual call fails clearly until the env vars are set.
        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        var configured = !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey);

        ChatClient chatClient = new AzureOpenAIClient(
                new Uri(configured ? endpoint! : "https://erda-unconfigured.invalid"),
                new ApiKeyCredential(configured ? apiKey! : "unconfigured"))
            .GetChatClient(options.ChatDeployment);

        var tools = new List<AITool>();
        tools.AddRange(services.GetRequiredService<ObsidianTools>().AsTools());
        tools.Add(VoiceMemoWorkflow.CreateTool(services));
        tools.AddRange(services.GetRequiredService<ReasoningTools>().AsTools());
        tools.AddRange(services.GetRequiredService<Erda.WhatsApp.NotifyTools>().AsTools());

        // The agent's name MUST equal the registration key (see Program.cs AddAIAgent).
        var agent = chatClient.AsAIAgent(instructions: Instructions, name: Name, tools: tools);

        // Instrument with OpenTelemetry (MAF builder): spans for the agent run, the model call
        // (token usage), and each tool/function invocation, emitted on ActivitySourceName.
        // EnableSensitiveData records prompts + tool arguments; gated by the config flag (off in
        // production), mirroring the OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT env var.
        return agent
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = observability.CaptureMessageContent)
            .Build();
    }
}
