using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class HealthCheckSchedulerTests
{
    private const string OwnerJid = "4915123456789@s.whatsapp.net";

    private static HealthCheckScheduler Build(
        FakeReasoner reasoner, FakeWhatsAppSender sender, IClock clock, FakeActivityRecorder recorder) =>
        new(
            Options.Create(new HealthCheckOptions()),
            Options.Create(new WhatsAppOptions { OwnerNumber = "+49 151 2345 6789" }),
            reasoner, sender, recorder, clock, NullLogger<HealthCheckScheduler>.Instance);

    private static (HealthCheckScheduler Scheduler, FakeReasoner Reasoner, FakeWhatsAppSender Sender, FakeActivityRecorder Recorder, FakeClock Clock) Make()
    {
        var reasoner = new FakeReasoner();
        var sender = new FakeWhatsAppSender();
        var recorder = new FakeActivityRecorder();
        var clock = new FakeClock();
        return (Build(reasoner, sender, clock, recorder), reasoner, sender, recorder, clock);
    }

    [Fact]
    public async Task Healthy_probe_sends_nothing()
    {
        var (scheduler, reasoner, sender, _, _) = Make();
        reasoner.Result = "ok";

        await scheduler.CheckOnceAsync(new HealthCheckOptions(), OwnerJid, default);

        Assert.Empty(sender.Sent);
        Assert.Single(reasoner.Calls);
        // Probe uses no web search and low effort.
        Assert.False(reasoner.Calls[0].WebSearch);
        Assert.Equal("low", reasoner.Calls[0].ReasoningEffort);
    }

    [Fact]
    public async Task Failed_probe_alerts_once()
    {
        var (scheduler, reasoner, sender, recorder, _) = Make();
        reasoner.Throw = new HttpRequestException("connection refused");
        var opts = new HealthCheckOptions();

        await scheduler.CheckOnceAsync(opts, OwnerJid, default);

        var (to, text) = Assert.Single(sender.Sent);
        Assert.Equal(OwnerJid, to);
        Assert.Contains("connection", text);
        Assert.Contains("failed", text);
        Assert.Contains(("health_check", "OpenAI/chat connection down"), recorder.Records.Select(r => (r.Kind, r.Summary)));
    }

    [Fact]
    public async Task Empty_response_counts_as_failure()
    {
        var (scheduler, reasoner, sender, _, _) = Make();
        reasoner.Result = "   ";

        await scheduler.CheckOnceAsync(new HealthCheckOptions(), OwnerJid, default);

        var (_, text) = Assert.Single(sender.Sent);
        Assert.Contains("empty", text);
    }

    [Fact]
    public async Task Ongoing_outage_does_not_re_alert_without_ReAlertAfter()
    {
        var (scheduler, reasoner, sender, _, _) = Make();
        reasoner.Throw = new HttpRequestException("down");
        var opts = new HealthCheckOptions(); // ReAlertAfter unset

        await scheduler.CheckOnceAsync(opts, OwnerJid, default);
        await scheduler.CheckOnceAsync(opts, OwnerJid, default);
        await scheduler.CheckOnceAsync(opts, OwnerJid, default);

        Assert.Single(sender.Sent); // alerted only on the down transition
    }

    [Fact]
    public async Task Ongoing_outage_re_alerts_after_cooldown()
    {
        var (scheduler, reasoner, sender, _, clock) = Make();
        reasoner.Throw = new HttpRequestException("down");
        var opts = new HealthCheckOptions { ReAlertAfter = TimeSpan.FromHours(6) };

        await scheduler.CheckOnceAsync(opts, OwnerJid, default); // down alert
        clock.UtcNow = clock.UtcNow.AddHours(3);
        await scheduler.CheckOnceAsync(opts, OwnerJid, default); // within cooldown -> quiet
        Assert.Single(sender.Sent);

        clock.UtcNow = clock.UtcNow.AddHours(4); // 7h since first alert
        await scheduler.CheckOnceAsync(opts, OwnerJid, default); // cooldown elapsed -> re-alert
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("still down", sender.Sent[1].Text);
    }

    [Fact]
    public async Task Recovery_sends_a_note_after_a_failure()
    {
        var (scheduler, reasoner, sender, recorder, clock) = Make();
        var opts = new HealthCheckOptions();

        reasoner.Throw = new HttpRequestException("down");
        await scheduler.CheckOnceAsync(opts, OwnerJid, default); // down
        clock.UtcNow = clock.UtcNow.AddMinutes(30);

        reasoner.Throw = null;
        reasoner.Result = "ok";
        await scheduler.CheckOnceAsync(opts, OwnerJid, default); // recovery

        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("recovered", sender.Sent[1].Text);
        Assert.Contains(("health_check", "OpenAI/chat connection recovered"), recorder.Records.Select(r => (r.Kind, r.Summary)));
    }

    [Fact]
    public async Task First_probe_healthy_then_a_later_failure_alerts()
    {
        var (scheduler, reasoner, sender, _, _) = Make();
        var opts = new HealthCheckOptions();

        reasoner.Result = "ok";
        await scheduler.CheckOnceAsync(opts, OwnerJid, default);
        Assert.Empty(sender.Sent);

        reasoner.Throw = new HttpRequestException("down");
        await scheduler.CheckOnceAsync(opts, OwnerJid, default);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task No_owner_configured_swallows_the_alert()
    {
        var (scheduler, reasoner, sender, _, _) = Make();
        reasoner.Throw = new HttpRequestException("down");

        await scheduler.CheckOnceAsync(new HealthCheckOptions(), ownerJid: "", default);

        Assert.Empty(sender.Sent);
    }
}
