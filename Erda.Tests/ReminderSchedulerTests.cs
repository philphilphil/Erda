using Erda.Configuration;
using Erda.Scheduling;
using Erda.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class ReminderSchedulerTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private const string OwnerJid = "4915123456789@s.whatsapp.net";

    // June → Berlin is UTC+2, so 08:00Z == 10:00 local.
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    private static ReminderOptions Opts() => new() { NotePath = "Reminders.md", NotifyOnError = true };

    private static (ReminderScheduler Scheduler, ReminderStore Store, FakeWhatsAppSender Sender, FakeAgentResponder Responder, ReminderStateStore StateStore) Make()
    {
        var vaultDir = Path.Combine(Path.GetTempPath(), "erda-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(Options.Create(new ErdaOptions { VaultPath = vaultDir }));
        var rOpts = Options.Create(Opts());
        var store = new ReminderStore(vault, rOpts, NullLogger<ReminderStore>.Instance);
        var sender = new FakeWhatsAppSender();
        var responder = new FakeAgentResponder();
        var timeContext = new CurrentTimeContext(new FakeClock(), rOpts);
        var scheduler = new ReminderScheduler(
            rOpts, Options.Create(new WhatsAppOptions { OwnerNumber = "+49 151 2345 6789" }),
            store, responder, sender, new FakeClock(), timeContext, NullLogger<ReminderScheduler>.Instance);
        var stateStore = new ReminderStateStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        return (scheduler, store, sender, responder, stateStore);
    }

    [Fact]
    public async Task Due_one_shot_reminder_is_sent_verbatim_and_marked_done()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom"); // 07:00Z, due
        var state = new ReminderState();

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Single(sender.Sent);
        Assert.Equal("Call mom", sender.Sent[0].Text);
        Assert.Contains("call-mom", state.FiredOneShotIds);
        Assert.Equal(ReminderStatus.Done, store.LoadAll().Reminders.Single(r => r.Id == "call-mom").Status);
    }

    [Fact]
    public async Task Future_one_shot_is_not_sent()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Reminder, "later", "2026-06-15 12:00", "Later"); // 10:00Z, future
        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Overdue_beyond_grace_is_marked_done_without_sending()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Reminder, "old", "2026-06-10 09:00", "Old"); // 5 days ago
        var state = new ReminderState();

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Empty(sender.Sent);
        Assert.Contains("old", state.FiredOneShotIds);
        Assert.Equal(ReminderStatus.Done, store.LoadAll().Reminders.Single(r => r.Id == "old").Status);
    }

    [Fact]
    public async Task Recurring_first_sight_seeds_without_firing()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?");
        var state = new ReminderState();

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Empty(sender.Sent);
        Assert.Equal(Now, state.LastFiredUtc["weather"]);
    }

    [Fact]
    public async Task Recurring_prompt_fires_when_occurrence_passed_then_advances()
    {
        var (s, store, sender, responder, ss) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?"); // 06:00 local == 04:00Z
        var state = new ReminderState { LastFiredUtc = { ["weather"] = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Single(responder.RunOnceCalls);
        Assert.Single(sender.Sent);
        Assert.StartsWith("⏰", sender.Sent[0].Text);
        Assert.Equal(Now, state.LastFiredUtc["weather"]); // advanced to now
    }

    [Fact]
    public async Task Recurring_does_not_backfill_after_a_long_gap()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?");
        var state = new ReminderState { LastFiredUtc = { ["weather"] = Now.AddDays(-10) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Single(sender.Sent); // exactly one, not ten
        Assert.Equal(Now, state.LastFiredUtc["weather"]);
    }

    [Fact]
    public async Task Paused_reminder_is_skipped()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");
        store.SetStatus("call-mom", ReminderStatus.Paused);

        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Already_fired_one_shot_is_not_resent()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");
        var state = new ReminderState { FiredOneShotIds = { "call-mom" } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Malformed_row_notifies_once()
    {
        var (s, store, sender, _, ss) = Make();
        store.Append(ReminderKind.Reminder, "bad", "not-a-time", "broken");

        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);
        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);

        Assert.Single(sender.Sent); // notified once, deduped on the second poll
        Assert.Contains("parse", sender.Sent[0].Text, StringComparison.OrdinalIgnoreCase);
    }
}
