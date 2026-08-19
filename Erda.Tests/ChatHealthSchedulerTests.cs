using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// The hourly probe of the local OpenAI proxy: one alert when it stops answering, an optional nag
/// while it stays down, and one notice when it comes back — never a page for a single blip.
/// </summary>
public class ChatHealthSchedulerTests
{
    private const string OwnerJid = "4915123456789@s.whatsapp.net";

    private static ChatHealthOptions Opts(TimeSpan? reAlertAfter = null) => new()
    {
        Enabled = true,
        CheckInterval = TimeSpan.FromHours(1),
        Timeout = TimeSpan.FromMinutes(2),
        ReAlertAfter = reAlertAfter,
    };

    private static (ChatHealthScheduler Scheduler, FakeChatHealthProbe Probe, FakeWhatsAppSender Sender,
        FakeActivityRecorder Recorder, FakeClock Clock) Make()
    {
        var probe = new FakeChatHealthProbe();
        var sender = new FakeWhatsAppSender();
        var recorder = new FakeActivityRecorder();
        var clock = new FakeClock();
        var scheduler = new ChatHealthScheduler(
            Options.Create(Opts()),
            Options.Create(new ErdaOptions
            {
                VaultPath = "/vault",
                DbPath = "/db",
                ChatBaseUrl = "http://127.0.0.1:10531/v1",
                ChatModel = "gpt-5.5",
            }),
            Options.Create(new WhatsAppOptions { OwnerNumber = "+49 151 2345 6789" }),
            probe, sender, recorder, clock, NullLogger<ChatHealthScheduler>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
            StartupDelay = TimeSpan.Zero,
        };
        return (scheduler, probe, sender, recorder, clock);
    }

    [Fact]
    public async Task Healthy_endpoint_sends_nothing()
    {
        var (scheduler, _, sender, _, _) = Make();

        await scheduler.CheckOnceAsync(Opts(), new ChatHealthState(), OwnerJid, default);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_single_failed_probe_is_retried_and_a_recovering_second_probe_stays_quiet()
    {
        var (scheduler, probe, sender, _, _) = Make();
        probe.ResultQueue.Enqueue(new ChatProbeResult(false, "blip", TimeSpan.FromSeconds(1)));
        // Falls back to the healthy default on the retry.

        var state = new ChatHealthState();
        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);

        Assert.Equal(2, probe.Calls);
        Assert.Empty(sender.Sent);
        Assert.Null(state.DownSince);
    }

    [Fact]
    public async Task Both_probes_failing_alerts_once_and_then_stays_quiet()
    {
        var (scheduler, probe, sender, _, clock) = Make();
        probe.Fail("HttpRequestException: Connection refused");
        var state = new ChatHealthState();

        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);

        var alert = Assert.Single(sender.Sent);
        Assert.Equal(OwnerJid, alert.To);
        Assert.Contains("OpenAI proxy is not answering", alert.Text);
        Assert.Contains("http://127.0.0.1:10531/v1", alert.Text);
        Assert.Contains("gpt-5.5", alert.Text);
        Assert.Contains("Connection refused", alert.Text);
        Assert.Equal(clock.UtcNow, state.DownSince);

        // Still down an hour later, no ReAlertAfter configured -> no second message.
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Ongoing_outage_re_alerts_once_the_cooldown_elapsed()
    {
        var (scheduler, probe, sender, _, clock) = Make();
        probe.Fail();
        var opts = Opts(reAlertAfter: TimeSpan.FromHours(6));
        var state = new ChatHealthState();

        await scheduler.CheckOnceAsync(opts, state, OwnerJid, default);
        Assert.Single(sender.Sent);

        // Inside the cooldown: quiet.
        clock.UtcNow = clock.UtcNow.AddHours(5);
        await scheduler.CheckOnceAsync(opts, state, OwnerJid, default);
        Assert.Single(sender.Sent);

        // Past it: one nag, naming how long it has been down.
        clock.UtcNow = clock.UtcNow.AddHours(2);
        await scheduler.CheckOnceAsync(opts, state, OwnerJid, default);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("still down (7 hours)", sender.Sent[1].Text);
    }

    [Fact]
    public async Task Recovery_notice_follows_an_alerted_outage_and_clears_the_state()
    {
        var (scheduler, probe, sender, recorder, clock) = Make();
        probe.Fail();
        var state = new ChatHealthState();
        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);

        clock.UtcNow = clock.UtcNow.AddMinutes(90);
        probe.Succeed();
        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);

        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("answering again", sender.Sent[1].Text);
        Assert.Contains("1 hour", sender.Sent[1].Text);
        Assert.Null(state.DownSince);
        Assert.Null(state.LastAlerted);
        Assert.Contains(recorder.Records, e => e.Kind == "chat_health" && e.Summary.Contains("recovered"));

        // Healthy again -> nothing more.
        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task An_outage_nobody_was_told_about_recovers_silently()
    {
        var (scheduler, probe, sender, _, clock) = Make();
        probe.Fail();
        var state = new ChatHealthState();

        // No owner configured: the outage is recorded but never delivered.
        await scheduler.CheckOnceAsync(Opts(), state, ownerJid: "", default);
        Assert.Empty(sender.Sent);
        Assert.NotNull(state.DownSince);
        Assert.Null(state.LastAlerted);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        probe.Succeed();
        await scheduler.CheckOnceAsync(Opts(), state, OwnerJid, default);

        Assert.Empty(sender.Sent);
        Assert.Null(state.DownSince);
    }

    [Fact]
    public async Task An_undeliverable_alert_still_starts_the_cooldown()
    {
        var (scheduler, probe, sender, _, clock) = Make();
        probe.Fail();
        sender.Result = false; // the bridge is down too
        var opts = Opts(reAlertAfter: TimeSpan.FromHours(6));
        var state = new ChatHealthState();

        await scheduler.CheckOnceAsync(opts, state, OwnerJid, default);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await scheduler.CheckOnceAsync(opts, state, OwnerJid, default);

        Assert.Single(sender.Sent);
        Assert.Equal(clock.UtcNow.AddHours(-1), state.LastAlerted);
    }

    [Fact]
    public async Task Probe_gets_the_configured_timeout()
    {
        var (scheduler, probe, _, _, _) = Make();
        var opts = Opts();
        opts.Timeout = TimeSpan.FromSeconds(45);

        await scheduler.CheckOnceAsync(opts, new ChatHealthState(), OwnerJid, default);

        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(probe.Timeouts));
    }

    [Fact]
    public async Task Disabled_watch_never_probes()
    {
        var (scheduler, probe, _, _, _) = Make();
        var disabled = new ChatHealthScheduler(
            Options.Create(new ChatHealthOptions { Enabled = false }),
            Options.Create(new ErdaOptions { VaultPath = "/vault", DbPath = "/db" }),
            Options.Create(new WhatsAppOptions()),
            probe, new FakeWhatsAppSender(), new FakeActivityRecorder(), new FakeClock(),
            NullLogger<ChatHealthScheduler>.Instance);

        await disabled.StartAsync(default);
        await disabled.StopAsync(default);

        Assert.Equal(0, probe.Calls);
    }
}
