using Erda.Agents;
using Erda.Core;
using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Server.Api;
using Erda.Server.Hosting;
using Erda.Server.WhatsApp;
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
        // EF Core logs every executed SQL statement at Information under this category — far too
        // noisy for the console and Seq. Keep its warnings/errors, drop the per-command spam.
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("app", "Erda") // tag every event so Erda's logs are filterable in Seq
        .WriteTo.Console();

    var seq = context.Configuration.GetSection(SeqOptions.SectionName).Get<SeqOptions>();
    if (seq is { IngestToErda: true } && !string.IsNullOrWhiteSpace(seq.ServerUrl))
        configuration.WriteTo.Seq(seq.ServerUrl, apiKey: string.IsNullOrWhiteSpace(seq.ApiKey) ? null : seq.ApiKey);
});

// --- SQLite database path --------------------------------------------------
// One file holds prompt versions, reminders (+ run-state), error-watch state, the activity feed,
// and config overrides. Path is bind-mounted in the container (Erda:DbPath) so it survives
// redeploys; otherwise LocalApplicationData/erda/erda.db. The path is needed here (before the DI
// container) for the SQLite config-override provider, and is handed to AddErdaCore.
var dbPath = builder.Configuration[$"{ErdaOptions.SectionName}:{nameof(ErdaOptions.DbPath)}"];
if (string.IsNullOrWhiteSpace(dbPath))
    dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erda", "erda.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

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

// --- Application services ---------------------------------------------------
// Core = config, DB, shared services, and the three background workers. Agents = the MAF tools,
// voice-memo workflow, and the agent responder.
builder.Services.AddErdaCore(builder.Configuration, dbPath);
builder.Services.AddErdaAgents();

// --- Control panel (Vue SPA + JSON API, LAN-only) --------------------------
// A single-user web UI: a Vue SPA (built by Vite, served as static files) talking to the JSON API
// over cookie auth. Cookie auth is off by default (open on the LAN) and turns on when
// Panel:Password is set.
builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection(PanelOptions.SectionName));
builder.Services.AddPanelApi();

// --- Agent & DevUI transport (discovered by DevUI) -------------------------
// Erda is the single orchestrator agent (gpt-5-mini on Azure Foundry, key auth). The agent's name
// MUST equal the registration key. DevUI rides on the OpenAI-compatible endpoints below.
builder.AddAIAgent(ErdaAgent.Name, (sp, _) => ErdaAgent.Create(sp));
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

// Control panel: serve the Vue SPA's static assets (wwwroot, populated by the Vite build in the
// Docker image), then cookie auth, then the JSON API. The SPA owns client-side routing, so unmatched
// non-file paths fall back to index.html in Production. In Development the SPA runs on the Vite dev
// server (which proxies /api here), so the backend serves no built SPA and "/" lands on DevUI.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPanelApi();

if (app.Environment.IsDevelopment())
    app.MapGet("/", () => Results.Redirect("/devui"));
else
    app.MapFallbackToFile("index.html");

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
