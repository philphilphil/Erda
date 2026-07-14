using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class StartupNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private static StartupNotifier Make(FakeWhatsAppSender sender, bool enabled = true, string owner = "+49 151 2345 6789")
        => new(Options.Create(new WhatsAppOptions { Enabled = enabled, OwnerNumber = owner }),
            sender, new FakeClock { UtcNow = Now }, NullLogger<StartupNotifier>.Instance)
        { RetryDelay = TimeSpan.FromMilliseconds(1) };

    private static async Task RunAsync(StartupNotifier notifier)
    {
        await notifier.StartAsync(CancellationToken.None);
        await notifier.ExecuteTask!;
    }

    [Fact]
    public void Compose_with_sha_and_build_time_includes_short_sha_and_age()
    {
        var text = StartupNotifier.ComposeNotice(
            "d476751abcdef0123456789", "2026-07-14T11:35:00Z", Now);

        Assert.Contains("sha-d476751", text);
        Assert.Contains("2026-07-14 11:35 UTC", text);
        Assert.Contains("25 minutes ago", text);
    }

    [Fact]
    public void Compose_reports_hours_and_days_for_older_builds()
    {
        Assert.Contains("5 hours ago",
            StartupNotifier.ComposeNotice("abc1234", Now.AddHours(-5).ToString("O"), Now));
        Assert.Contains("3 days ago",
            StartupNotifier.ComposeNotice("abc1234", Now.AddDays(-3).ToString("O"), Now));
    }

    [Fact]
    public void Compose_without_sha_reports_dev_build()
    {
        var text = StartupNotifier.ComposeNotice(null, null, Now);
        Assert.Contains("dev (local build)", text);
    }

    [Fact]
    public void Compose_with_unparsable_build_time_omits_age()
    {
        var text = StartupNotifier.ComposeNotice("abc1234", "not-a-date", Now);
        Assert.Contains("sha-abc1234", text);
        Assert.DoesNotContain("built", text);
    }

    [Fact]
    public async Task Sends_boot_notice_to_the_owner()
    {
        var sender = new FakeWhatsAppSender();
        await RunAsync(Make(sender));

        var (to, text) = Assert.Single(sender.Sent);
        Assert.Equal("4915123456789@s.whatsapp.net", to);
        Assert.Contains("Erda is up", text);
    }

    [Fact]
    public async Task Retries_until_the_send_succeeds()
    {
        var sender = new FakeWhatsAppSender();
        sender.ResultQueue.Enqueue(false);
        sender.ResultQueue.Enqueue(false);
        sender.ResultQueue.Enqueue(true);

        await RunAsync(Make(sender));
        Assert.Equal(3, sender.Sent.Count);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var sender = new FakeWhatsAppSender { Result = false };
        await RunAsync(Make(sender));
        Assert.Equal(StartupNotifier.MaxAttempts, sender.Sent.Count);
    }

    [Fact]
    public async Task Does_nothing_when_whatsapp_is_disabled()
    {
        var sender = new FakeWhatsAppSender();
        await RunAsync(Make(sender, enabled: false));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Does_nothing_without_an_owner_number()
    {
        var sender = new FakeWhatsAppSender();
        await RunAsync(Make(sender, owner: ""));
        Assert.Empty(sender.Sent);
    }
}
