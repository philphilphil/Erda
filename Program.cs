using Erda.Agents;
using Erda.Channels;
using Erda.Configuration;
using Erda.Scheduling;
using Erda.Services;
using Erda.Services.Seq;
using Erda.Tools;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Logging: Serilog (console + optional Seq sink) ------------------------
// Erda ships its own logs to the same Seq the error-watch scheduler reads, so Erda's own errors
// show up there too. The Seq sink is only added when Seq:ServerUrl is set and Seq:IngestToErda.
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    var seq = context.Configuration.GetSection(SeqOptions.SectionName).Get<SeqOptions>();
    if (seq is { IngestToErda: true } && !string.IsNullOrWhiteSpace(seq.ServerUrl))
        configuration.WriteTo.Seq(seq.ServerUrl, apiKey: string.IsNullOrWhiteSpace(seq.ApiKey) ? null : seq.ApiKey);
});

// --- Configuration ---------------------------------------------------------
builder.Services.Configure<ErdaOptions>(builder.Configuration.GetSection(ErdaOptions.SectionName));

// --- Erda services ---------------------------------------------------------
builder.Services.AddSingleton<VaultService>();
builder.Services.AddSingleton<ObsidianTools>();
builder.Services.AddSingleton<ReasoningTools>();
builder.Services.AddSingleton<Transcriber>();
builder.Services.AddSingleton<ITranscriber>(sp => sp.GetRequiredService<Transcriber>());
builder.Services.AddSingleton<CodexRunner>();

// --- WhatsApp channel -------------------------------------------------------
// A whatsmeow "bridge" sidecar holds the WhatsApp socket; Erda exposes an inbound endpoint it
// POSTs to, and calls the bridge's /send for replies + proactive messages. The owner whitelist
// and all model/credential work stay here; the bridge is a dumb relay.
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection(WhatsAppOptions.SectionName));
builder.Services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();
builder.Services.AddSingleton<NotifyTools>();
builder.Services.AddSingleton<IAgentResponder, ErdaAgentResponder>();
builder.Services.AddSingleton<WhatsAppInboundQueue>();
builder.Services.AddSingleton<WhatsAppChannelService>();
builder.Services.AddHostedService<WhatsAppInboundWorker>();

// --- Error-watch scheduler (Seq -> Codex -> WhatsApp) -----------------------
// Polls the (remote) Seq server for new Error/Fatal events, asks Codex to analyze each new one,
// and pushes the analysis to Phil over WhatsApp. Gated by ErrorWatch:Enabled + Seq:ServerUrl.
builder.Services.Configure<SeqOptions>(builder.Configuration.GetSection(SeqOptions.SectionName));
builder.Services.Configure<ErrorWatchOptions>(builder.Configuration.GetSection(ErrorWatchOptions.SectionName));
builder.Services.AddSingleton<ISeqClient, SeqClient>();
builder.Services.AddSingleton<IErrorAnalyzer, CodexErrorAnalyzer>();
builder.Services.AddHostedService<ErrorWatchScheduler>();

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

// Inbound WhatsApp bridge endpoint (only mapped when WhatsApp:Enabled).
app.MapWhatsAppChannel();

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
