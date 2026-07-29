using Erda.Agents;
using Erda.Core;
using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Erda.Server.Api;
using Erda.Server.Hosting;
using Erda.Server.Upload;
using Erda.Server.WhatsApp;
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
        // HttpClientFactory's two logging handlers log every outbound request (start/send/receive) at
        // Information, and on failure log the full exception twice — so one unreachable bridge POST
        // dumps two stack traces on top of the caller's own Warning. Keep Warning+; drop the spam. The
        // app's own senders/fetchers log meaningful failures themselves.
        .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("app", "Erda") // tag every event so Erda's logs are filterable in Seq
        .WriteTo.Console();

    var seq = context.Configuration.GetSection(SeqOptions.SectionName).Get<SeqOptions>();
    if (seq is { IngestToErda: true } && !string.IsNullOrWhiteSpace(seq.ServerUrl))
        configuration.WriteTo.Seq(seq.ServerUrl, apiKey: string.IsNullOrWhiteSpace(seq.ApiKey) ? null : seq.ApiKey);
});

// --- SQLite database path --------------------------------------------------
// One file holds prompt versions, reminders (+ run-state), error-watch state, and the activity feed.
// Bind-mounted in the container so it survives redeploys. Required (no default) like every setting;
// read here, before the DI container, because the DbContext factory needs it at registration. The
// matching [Required] on ErdaOptions.DbPath also fails the app at startup if it's blank.
var dbPath = builder.Configuration[$"{ErdaOptions.SectionName}:{nameof(ErdaOptions.DbPath)}"];
if (string.IsNullOrWhiteSpace(dbPath))
    throw new InvalidOperationException(
        "Erda__DbPath is required — set it in .env (e.g. Erda__DbPath=/data/erda/erda.db).");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

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

// --- Agent ----------------------------------------------------------------
// Erda is the single orchestrator agent (gpt-5-mini on Azure Foundry, key auth). The agent's name
// MUST equal the registration key; it's resolved by keyed DI (ErdaAgentResponder, WebChatService).
builder.AddAIAgent(ErdaAgent.Name, (sp, _) => ErdaAgent.Create(sp));

var app = builder.Build();

// Create/upgrade the SQLite schema before anything reads it (first run creates erda.db).
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ErdaDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();

    // The inbound queue is in-memory: any voice-memo row still "pending" belongs to a previous process
    // and can never be processed, so retire it instead of showing it as pending forever in the panel.
    await scope.ServiceProvider.GetRequiredService<IVoiceMemoArchive>().ReconcileStalePendingAsync();
}

LogStartupConfig(app);

// Control panel: serve the Vue SPA's static assets (wwwroot, populated by the Vite build in the
// Docker image), then cookie auth, then the JSON API. The SPA owns client-side routing, so unmatched
// non-file paths fall back to index.html. In Development the SPA runs on the Vite dev server (which
// proxies /api here), so wwwroot is empty and the backend serves no SPA — use the Vite URL directly.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPanelApi();

app.MapFallbackToFile("index.html");

// Inbound WhatsApp bridge endpoint (only mapped when WhatsApp:Enabled).
app.MapWhatsAppChannel();

// HTTP audio upload (iOS Shortcut → same voice-memo pipeline; only mapped when Upload:Enabled).
app.MapUploadEndpoint();

// Connect the browser MCP before the host starts. The orchestrator agent is built when the
// background services (e.g. ReminderScheduler) are constructed during host start, and it reads
// IBrowserMcp.Tools at that moment — so the MCP must be connected first. No-op when disabled.
await app.Services.GetRequiredService<Erda.Agents.Tools.IBrowserMcp>().EnsureStartedAsync();

app.Run();

// Print the resolved (non-secret) config. Credentials are validated at startup (ValidateOnStart),
// so by the time this runs they are guaranteed present — no need to warn about missing keys here.
static void LogStartupConfig(WebApplication app)
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    var opts = app.Services.GetRequiredService<IOptions<ErdaOptions>>().Value;

    log.LogInformation(
        "Erda config: vault={Vault}, db={Db}, chatBaseUrl={ChatBaseUrl}, chatModel={ChatModel}/{Effort}, transcribeModel={Transcribe}, voiceMemoSubfolder={Sub}",
        opts.VaultPath, opts.DbPath, opts.ChatBaseUrl, opts.ChatModel, opts.ChatReasoningEffort, opts.TranscribeModel, opts.VoiceMemoSubfolder);
}
