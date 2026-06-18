using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Core.Services.OnePassword;
using Erda.Core.Services.Seq;
using Erda.Core.Upload;
using Erda.Core.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erda.Core;

/// <summary>
/// DI wiring for the host-agnostic core: configuration options, the SQLite store, the shared
/// services, and the three background workers (WhatsApp inbound, error-watch, reminders). The host
/// supplies the resolved <paramref name="dbPath"/> (it also needs it before the container exists,
/// for the SQLite config-override provider).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddErdaCore(
        this IServiceCollection services, IConfiguration configuration, string dbPath)
    {
        // --- Options ---
        // Env vars are the single source. Required settings carry no default and are validated at
        // startup (ValidateOnStart) so a missing value stops the app with a clear, aggregated error
        // instead of failing later. Feature settings (WhatsApp, Browser) are only required when their
        // Enabled switch is on — see the IValidateOptions validators below.

        // Credentials are flat env vars (AZURE_OPENAI_*, OPENAI_API_KEY), so bind the config root.
        services.AddOptions<CredentialsOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ErdaOptions>()
            .Bind(configuration.GetSection(ErdaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<WhatsAppOptions>()
            .Bind(configuration.GetSection(WhatsAppOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WhatsAppOptions>, WhatsAppOptionsValidator>();

        services.AddOptions<BrowserOptions>()
            .Bind(configuration.GetSection(BrowserOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BrowserOptions>, BrowserOptionsValidator>();

        services.AddOptions<UploadOptions>()
            .Bind(configuration.GetSection(UploadOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<UploadOptions>, UploadOptionsValidator>();

        services.AddOptions<ErrorWatchOptions>()
            .Bind(configuration.GetSection(ErrorWatchOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ErrorWatchOptions>, ErrorWatchOptionsValidator>();

        services.AddOptions<ReminderOptions>()
            .Bind(configuration.GetSection(ReminderOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReminderOptions>, ReminderOptionsValidator>();

        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));
        services.Configure<SeqOptions>(configuration.GetSection(SeqOptions.SectionName));

        // --- SQLite database (all runtime state) ---
        // Consumers are singletons/background services, so they take an IDbContextFactory and open a
        // short-lived context per operation.
        services.AddDbContextFactory<ErdaDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        // --- Shared services ---
        services.AddSingleton<VaultService>();
        services.AddSingleton<Transcriber>();
        services.AddSingleton<ITranscriber>(sp => sp.GetRequiredService<Transcriber>());
        services.AddSingleton<CodexRunner>();
        // Other consumers use the concrete CodexRunner; the reminder scheduler takes the interface
        // so its Codex-direct branch is unit-testable with a fake.
        services.AddSingleton<ICodexRunner>(sp => sp.GetRequiredService<CodexRunner>());
        services.AddSingleton<PreScriptRunner>();
        services.AddSingleton<IPreScriptRunner>(sp => sp.GetRequiredService<PreScriptRunner>());
        // --- 1Password (op CLI + secret resolver) ---
        services.AddSingleton<IOpCli, OpCli>();
        services.AddSingleton<IOpSecretResolver, OpSecretResolver>();
        // URL fetcher for the recipe-importer workflow (creates clients via the factory per fetch).
        services.AddHttpClient(nameof(UrlFetcher));
        services.AddSingleton<IUrlFetcher, UrlFetcher>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<CurrentTimeContext>();
        services.AddSingleton<IPromptStore, PromptStore>();
        services.AddSingleton<IActivityRecorder, ActivityRecorder>();

        // --- WhatsApp channel ---
        // A whatsmeow "bridge" sidecar holds the WhatsApp socket; Erda POSTs replies to its /send and
        // drains an inbound queue. The owner whitelist + all model work stay here; the bridge relays.
        services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();
        services.AddSingleton<WhatsAppInboundQueue>();
        services.AddSingleton<WhatsAppChannelService>();
        services.AddHostedService<WhatsAppInboundWorker>();

        // --- HTTP upload intake (iOS Shortcut → same voice-memo pipeline) ---
        // Saves the uploaded audio and enqueues it onto the WhatsApp inbound queue above, so the
        // worker handles it exactly like a shared Apple Voice Memo. Requires WhatsApp enabled (the
        // reply goes back over the bridge); enforced where POST /upload is mapped.
        services.AddSingleton<UploadIntake>();

        // --- Error-watch scheduler (Seq -> Codex -> WhatsApp) ---
        services.AddSingleton<ISeqClient, SeqClient>();
        services.AddSingleton<IErrorAnalyzer, CodexErrorAnalyzer>();
        services.AddSingleton<ErrorWatchStateStore>();
        services.AddHostedService<ErrorWatchScheduler>();

        // --- Reminder scheduler (DB row -> WhatsApp / agent prompt) ---
        services.AddSingleton<ReminderStore>();
        services.AddSingleton<ReminderStateStore>();
        services.AddHostedService<ReminderScheduler>();

        return services;
    }
}
