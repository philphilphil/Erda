using Erda.Agents;
using Erda.Configuration;
using Erda.Services;
using Erda.Tools;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
builder.Services.Configure<ErdaOptions>(builder.Configuration.GetSection(ErdaOptions.SectionName));

// --- Erda services ---------------------------------------------------------
builder.Services.AddSingleton<VaultService>();
builder.Services.AddSingleton<ObsidianTools>();
builder.Services.AddSingleton<ReasoningTools>();
builder.Services.AddSingleton<Transcriber>();
builder.Services.AddSingleton<CodexRunner>();

// --- Agents & workflows (discovered by DevUI) ------------------------------
// Erda is the single orchestrator agent (gpt-5-mini on Azure Foundry, key auth). Its tools are
// the five Obsidian vault tools plus process_voice_memo, which is the voice-memo MAF workflow
// exposed via AsAIFunction. So DevUI shows just "erda", and Erda routes voice memos into the
// workflow rather than the workflow being a separate top-level agent.
builder.AddAIAgent(ErdaAgent.Name, (sp, _) => ErdaAgent.Create(sp));

// --- DevUI transport: OpenAI-compatible endpoints DevUI rides on -----------
builder.AddOpenAIResponses();
builder.AddOpenAIConversations();
if (builder.Environment.IsDevelopment())
    builder.AddDevUI();

var app = builder.Build();

LogStartupConfig(app);

app.MapOpenAIResponses();
app.MapOpenAIConversations();
if (app.Environment.IsDevelopment())
    app.MapDevUI(); // dashboard at /devui

app.MapGet("/", () => Results.Redirect("/devui"));

app.Run();

// Print the resolved config and which credentials are present, so missing keys are obvious.
static void LogStartupConfig(WebApplication app)
{
    var cfg = app.Configuration;
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    var opts = app.Services.GetRequiredService<IOptions<ErdaOptions>>().Value;

    static string State(string? v) => string.IsNullOrWhiteSpace(v) ? "MISSING" : "set";

    log.LogInformation(
        "Erda config: vault={Vault}, chatDeployment={Deployment}, transcribeModel={Transcribe}, codex={Codex}/{Effort}, voiceMemoSubfolder={Sub}",
        opts.VaultPath, opts.ChatDeployment, opts.TranscribeModel, opts.CodexModel, opts.CodexReasoningEffort, opts.VoiceMemoSubfolder);
    log.LogInformation(
        "Credentials: AZURE_OPENAI_ENDPOINT={A}, AZURE_OPENAI_API_KEY={B}, OPENAI_API_KEY={C}",
        State(cfg["AZURE_OPENAI_ENDPOINT"]), State(cfg["AZURE_OPENAI_API_KEY"]), State(cfg["OPENAI_API_KEY"]));

    if (string.IsNullOrWhiteSpace(cfg["AZURE_OPENAI_ENDPOINT"]) || string.IsNullOrWhiteSpace(cfg["AZURE_OPENAI_API_KEY"]))
        log.LogWarning("Chat agent will fail until AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY are set.");
    if (string.IsNullOrWhiteSpace(cfg["OPENAI_API_KEY"]))
        log.LogWarning("Voice-memo transcription will fail until OPENAI_API_KEY is set.");
}
