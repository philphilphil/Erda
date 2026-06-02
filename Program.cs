using Erda.Agents;
using Erda.Configuration;
using Erda.Data;
using Erda.Scheduling;
using Erda.Services;
using Erda.Services.Seq;
using Erda.Tools;
using Erda.WhatsApp;
using Erda.Workflows;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Observability content gate (set BEFORE the agent is instrumented) -----
// Default OFF: spans carry only metadata (tool names, durations, token counts). When
// Observability:CaptureMessageContent is true (it is in Development), also capture prompts and
// tool arguments. MAF reads this standard env var when instrumenting the agent.
var observability = builder.Configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>()
    ?? new ObservabilityOptions();
if (observability.CaptureMessageContent)
    Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");

// --- Logging: Serilog (console + optional Seq sink) ------------------------
// Erda ships its own logs to the same Seq the error-watch scheduler reads, so Erda's own errors
// show up there too. The Seq sink is only added when Seq:ServerUrl is set and Seq:IngestToErda.
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("app", "Erda") // tag every event so Erda's logs are filterable in Seq
        .WriteTo.Console();

    var seq = context.Configuration.GetSection(SeqOptions.SectionName).Get<SeqOptions>();
    if (seq is { IngestToErda: true } && !string.IsNullOrWhiteSpace(seq.ServerUrl))
        configuration.WriteTo.Seq(seq.ServerUrl, apiKey: string.IsNullOrWhiteSpace(seq.ApiKey) ? null : seq.ApiKey);
});

// --- Configuration ---------------------------------------------------------
builder.Services.Configure<ErdaOptions>(builder.Configuration.GetSection(ErdaOptions.SectionName));
builder.Services.Configure<ObservabilityOptions>(builder.Configuration.GetSection(ObservabilityOptions.SectionName));

// --- SQLite database (all runtime state) -----------------------------------
// One file holds prompt versions, reminders (+ run-state), error-watch state, the activity feed,
// and config overrides. Consumers are singletons/background services, so they take an
// IDbContextFactory and open a short-lived context per operation. Path is bind-mounted in the
// container (Erda:DbPath) so it survives redeploys; otherwise LocalApplicationData/erda/erda.db.
var dbPath = builder.Configuration[$"{ErdaOptions.SectionName}:{nameof(ErdaOptions.DbPath)}"];
if (string.IsNullOrWhiteSpace(dbPath))
    dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erda", "erda.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
builder.Services.AddDbContextFactory<ErdaDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

// Config overrides edited in the control panel live in the DB and are layered over appsettings/env
// here (read once at startup — they apply on restart). Safe before the DB exists: returns empty.
builder.Configuration.AddSqliteOverrides(dbPath);

// --- OpenTelemetry tracing -------------------------------------------------
// MAF emits spans per turn: agent run -> model call (token usage) -> each tool/function call.
// Exported to Seq over OTLP when Seq:ServerUrl is set; console exporter in Development for a
// zero-setup local view. Message content is gated by the env var set above.
if (observability.Enabled)
{
    var seqForOtel = builder.Configuration.GetSection(SeqOptions.SectionName).Get<SeqOptions>();
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName: "Erda"))
        .WithTracing(tracing =>
        {
            tracing.AddSource(ObservabilityOptions.ActivitySourceName);
            // Tag spans with a top-level app=Erda so traces are filterable alongside the Serilog
            // logs in Seq (resource attributes like service.name aren't). Must precede exporters.
            tracing.AddProcessor(new AppTagSpanProcessor("Erda"));
            if (builder.Environment.IsDevelopment())
                tracing.AddConsoleExporter();
            if (!string.IsNullOrWhiteSpace(seqForOtel?.ServerUrl))
                tracing.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri($"{seqForOtel!.ServerUrl!.TrimEnd('/')}/ingest/otlp/v1/traces");
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    if (!string.IsNullOrWhiteSpace(seqForOtel.ApiKey))
                        otlp.Headers = $"X-Seq-ApiKey={seqForOtel.ApiKey}";
                });
        });
}

// --- Erda services ---------------------------------------------------------
builder.Services.AddSingleton<VaultService>();
builder.Services.AddSingleton<ObsidianTools>();
builder.Services.AddSingleton<ReasoningTools>();
builder.Services.AddSingleton<Transcriber>();
builder.Services.AddSingleton<ITranscriber>(sp => sp.GetRequiredService<Transcriber>());
builder.Services.AddSingleton<CodexRunner>();
builder.Services.AddSingleton<MemoProcessor>();
builder.Services.AddSingleton<IMemoProcessor>(sp => sp.GetRequiredService<MemoProcessor>());
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<CurrentTimeContext>();
builder.Services.AddSingleton<IPromptStore, PromptStore>();
builder.Services.AddSingleton<IActivityRecorder, ActivityRecorder>();

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
builder.Services.AddSingleton<ErrorWatchStateStore>();
builder.Services.AddHostedService<ErrorWatchScheduler>();

// --- Reminder scheduler (vault note -> WhatsApp / agent prompt) -------------
// Every minute, read the reminders note and fire what's due: verbatim messages straight to Phil,
// or scheduled prompts run through the agent (fresh session) with the reply sent. Cron via Cronos,
// times in Reminders:TimeZone. Definitions live in the vault (Phil can hand-edit); run-state in a
// JSON sidecar. The schedule_* agent tools write to the same note.
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection(ReminderOptions.SectionName));
builder.Services.AddSingleton<ReminderStore>();
builder.Services.AddSingleton<ReminderStateStore>();
builder.Services.AddSingleton<ReminderTools>();
builder.Services.AddHostedService<ReminderScheduler>();

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

// Create/upgrade the SQLite schema before anything reads it (first run creates erda.db).
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ErdaDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();
}

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
