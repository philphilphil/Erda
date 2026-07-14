using System.Globalization;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.Options;

namespace Erda.Core.WhatsApp;

/// <summary>
/// Sends Phil a one-off WhatsApp message when Erda boots, naming the running image version (git sha
/// + build time, baked into the image by CI as <c>ERDA_GIT_SHA</c>/<c>ERDA_BUILD_TIME</c>; both are
/// empty on local builds). The bridge container boots in parallel and its WhatsApp socket takes a
/// few seconds, so failed sends are retried for a while. Best-effort: giving up only logs a warning.
/// </summary>
public sealed class StartupNotifier(
    IOptions<WhatsAppOptions> options,
    IWhatsAppSender sender,
    IClock clock,
    ILogger<StartupNotifier> logger) : BackgroundService
{
    public const int MaxAttempts = 12;

    /// <summary>Delay between send attempts; shortened by tests.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
            return;

        var ownerJid = WhatsAppJid.FromNumber(o.OwnerNumber);
        if (string.IsNullOrEmpty(ownerJid))
        {
            logger.LogWarning("Startup notifier: WhatsApp owner number not configured; skipping boot notice.");
            return;
        }

        var text = ComposeNotice(
            Environment.GetEnvironmentVariable("ERDA_GIT_SHA"),
            Environment.GetEnvironmentVariable("ERDA_BUILD_TIME"),
            clock.UtcNow);

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (await sender.SendAsync(ownerJid, text, stoppingToken))
                    return;
                if (attempt < MaxAttempts)
                    await Task.Delay(RetryDelay, stoppingToken);
            }
            logger.LogWarning("Startup notifier: bridge never accepted the boot notice after {Attempts} attempts.",
                MaxAttempts);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down mid-retry — nothing to do.
        }
    }

    /// <summary>The boot-notice text for the given baked-in image identity, e.g.
    /// "🚀 Erda is up. Version sha-d476751, image built 2026-07-14 06:41 UTC (25 minutes ago)."</summary>
    public static string ComposeNotice(string? gitSha, string? buildTime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(gitSha))
            return "🚀 Erda is up. Version dev (local build).";

        var sha = gitSha.Trim();
        if (sha.Length > 7)
            sha = sha[..7];

        if (!DateTimeOffset.TryParse(buildTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var built))
            return $"🚀 Erda is up. Version sha-{sha}.";

        return $"🚀 Erda is up. Version sha-{sha}, image built {built:yyyy-MM-dd HH:mm} UTC ({Age(now - built)}).";
    }

    private static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalHours < 1)
            return $"{(int)age.TotalMinutes} minutes ago";
        if (age.TotalHours < 48)
            return $"{(int)age.TotalHours} hours ago";
        return $"{(int)age.TotalDays} days ago";
    }
}
