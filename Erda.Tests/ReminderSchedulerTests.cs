using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
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

    private static (ReminderScheduler Scheduler, ReminderStore Store, FakeWhatsAppSender Sender, FakeAgentResponder Responder, ReminderStateStore StateStore, VaultService Vault) Make()
    {
        var dbf = TestDb.NewFactory();
        var vaultDir = Path.Combine(Path.GetTempPath(), "erda-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(Options.Create(new ErdaOptions { VaultPath = vaultDir }));
        var rOpts = Options.Create(Opts());
        var store = new ReminderStore(dbf, NullLogger<ReminderStore>.Instance);
        var stateStore = new ReminderStateStore(dbf);
        var sender = new FakeWhatsAppSender();
        var responder = new FakeAgentResponder();
        var timeContext = new CurrentTimeContext(new FakeClock(), rOpts);
        var scheduler = new ReminderScheduler(
            rOpts, Options.Create(new WhatsAppOptions { OwnerNumber = "+49 151 2345 6789" }),
            store, stateStore, vault, responder, sender, new FakeClock(), timeContext,
            new FakeActivityRecorder(), NullLogger<ReminderScheduler>.Instance);
        return (scheduler, store, sender, responder, stateStore, vault);
    }

    [Fact]
    public async Task Due_one_shot_reminder_is_sent_verbatim_and_marked_done()
    {
        var (s, store, sender, _, ss, _) = Make();
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
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Reminder, "later", "2026-06-15 12:00", "Later"); // 10:00Z, future
        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Overdue_beyond_grace_is_marked_done_without_sending()
    {
        var (s, store, sender, _, ss, _) = Make();
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
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?");
        var state = new ReminderState();

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Empty(sender.Sent);
        Assert.Equal(Now, state.LastFiredUtc["weather"]);
    }

    [Fact]
    public async Task Recurring_prompt_fires_when_occurrence_passed_then_advances()
    {
        var (s, store, sender, responder, ss, _) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?"); // 06:00 local == 04:00Z
        var state = new ReminderState { LastFiredUtc = { ["weather"] = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Single(responder.RunOnceCalls);
        Assert.Single(sender.Sent);
        Assert.StartsWith("⏰", sender.Sent[0].Text);
        Assert.Equal(Now, state.LastFiredUtc["weather"]); // advanced to now
    }

    [Fact]
    public async Task Prompt_starting_with_at_reads_the_vault_file_as_the_prompt()
    {
        var (s, store, sender, responder, ss, vault) = Make();
        vault.WriteNote("prompts/weather.md", "Give me the Munich weather, one concise line.");
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "@prompts/weather.md");
        var state = new ReminderState { LastFiredUtc = { ["weather"] = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Single(responder.RunOnceCalls);
        Assert.Contains(responder.RunOnceCalls[0], m => m.Text.Contains("Munich weather"));
    }

    [Fact]
    public async Task Prompt_at_path_resolves_without_the_md_extension()
    {
        var (s, store, sender, responder, ss, vault) = Make();
        vault.WriteNote("prompts/weather.md", "Munich weather please.");
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "@prompts/weather"); // no .md
        var state = new ReminderState { LastFiredUtc = { ["weather"] = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Contains(responder.RunOnceCalls[0], m => m.Text.Contains("Munich weather"));
    }

    [Fact]
    public async Task Prompt_with_missing_at_file_does_not_send()
    {
        var (s, store, sender, responder, ss, _) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "@prompts/nope.md");
        var state = new ReminderState { LastFiredUtc = { ["weather"] = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Empty(responder.RunOnceCalls);
        Assert.Contains(sender.Sent, m => m.Text.Contains("failed")); // failure notice, not a reply
    }

    [Fact]
    public async Task Verbatim_reminder_starting_with_at_is_sent_literally_not_as_a_file()
    {
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Reminder, "ping", "2026-06-15 09:00", "@everyone standup!");

        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);

        Assert.Single(sender.Sent);
        Assert.Equal("@everyone standup!", sender.Sent[0].Text);
    }

    [Fact]
    public async Task Recurring_does_not_backfill_after_a_long_gap()
    {
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?");
        var state = new ReminderState { LastFiredUtc = { ["weather"] = Now.AddDays(-10) } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);

        Assert.Single(sender.Sent); // exactly one, not ten
        Assert.Equal(Now, state.LastFiredUtc["weather"]);
    }

    [Fact]
    public async Task Paused_reminder_is_skipped()
    {
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");
        store.SetStatus("call-mom", ReminderStatus.Paused);

        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Already_fired_one_shot_is_not_resent()
    {
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");
        var state = new ReminderState { FiredOneShotIds = { "call-mom" } };

        await s.PollOnceAsync(Opts(), ss, state, Berlin, OwnerJid, Now, default);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Malformed_row_notifies_once()
    {
        var (s, store, sender, _, ss, _) = Make();
        store.Append(ReminderKind.Reminder, "bad", "not-a-time", "broken");

        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);
        await s.PollOnceAsync(Opts(), ss, new ReminderState(), Berlin, OwnerJid, Now, default);

        Assert.Single(sender.Sent); // notified once, deduped on the second poll
        Assert.Contains("parse", sender.Sent[0].Text, StringComparison.OrdinalIgnoreCase);
    }
}
