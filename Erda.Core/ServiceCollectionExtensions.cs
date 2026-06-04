using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Core.Services.Seq;
using Erda.Core.WhatsApp;
using Microsoft.EntityFrameworkCore;

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
        services.Configure<ErdaOptions>(configuration.GetSection(ErdaOptions.SectionName));
        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.Configure<SeqOptions>(configuration.GetSection(SeqOptions.SectionName));
        services.Configure<ErrorWatchOptions>(configuration.GetSection(ErrorWatchOptions.SectionName));
        services.Configure<ReminderOptions>(configuration.GetSection(ReminderOptions.SectionName));
        services.Configure<BrowserOptions>(configuration.GetSection("Erda:Browser"));

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
