using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class ReminderToolsTests
{
    private static (ReminderTools Tools, ReminderStore Store, VaultService Vault) Make()
    {
        var dbf = TestDb.NewFactory();
        var vaultDir = Path.Combine(Path.GetTempPath(), "erda-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(Options.Create(new ErdaOptions { VaultPath = vaultDir }));
        var opts = Options.Create(new ReminderOptions());
        var store = new ReminderStore(dbf, NullLogger<ReminderStore>.Instance);
        return (new ReminderTools(store, vault, opts, new FakeClock()), store, vault);
    }

    [Fact]
    public void Schedule_message_appends_a_reminder_row()
    {
        var (tools, store, _) = Make();
        var result = tools.ScheduleMessage("0 19 * * 0", "Take out the trash");

        Assert.Contains("Scheduled", result);
        var r = store.LoadAll().Reminders.Single();
        Assert.Equal(ReminderKind.Reminder, r.Kind);
        Assert.Equal("Take out the trash", r.Text);
    }

    [Fact]
    public void Schedule_prompt_appends_a_prompt_row()
    {
        var (tools, store, _) = Make();
        tools.SchedulePrompt("0 6 * * *", "What's the weather?");

        Assert.Equal(ReminderKind.Prompt, store.LoadAll().Reminders.Single().Kind);
    }

    [Fact]
    public void Rejects_an_invalid_when_without_appending()
    {
        var (tools, store, _) = Make();
        var result = tools.ScheduleMessage("whenever", "x");

        Assert.Contains("understand", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.LoadAll().Reminders);
    }

    [Fact]
    public void Generates_distinct_ids_for_same_text()
    {
        var (tools, store, _) = Make();
        tools.ScheduleMessage("0 8 * * *", "Drink water");
        tools.ScheduleMessage("0 20 * * *", "Drink water");

        var ids = store.LoadAll().Reminders.Select(r => r.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public void Schedule_prompt_warns_when_the_at_file_is_missing()
    {
        var (tools, _, _) = Make();
        var result = tools.SchedulePrompt("0 6 * * *", "@prompts/nope.md");
        Assert.Contains("couldn't find", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schedule_prompt_does_not_warn_when_the_at_file_exists()
    {
        var (tools, _, vault) = Make();
        vault.WriteNote("prompts/morning.md", "Summarize my day.");
        var result = tools.SchedulePrompt("0 6 * * *", "@prompts/morning.md");
        Assert.DoesNotContain("couldn't find", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancel_removes_a_reminder()
    {
        var (tools, store, _) = Make();
        tools.ScheduleMessage("0 8 * * *", "Drink water", "water");

        var result = tools.CancelScheduled("water");
        Assert.Contains("ancel", result); // "Cancelled"
        Assert.Empty(store.LoadAll().Reminders);
    }

    [Fact]
    public void Pause_sets_status_paused()
    {
        var (tools, store, _) = Make();
        tools.ScheduleMessage("0 8 * * *", "Drink water", "water");

        var result = tools.PauseScheduled("water");
        Assert.Contains("Paused", result);
        Assert.Equal(ReminderStatus.Paused, store.LoadAll().Reminders.Single().Status);
    }

    [Fact]
    public void Resume_sets_status_active()
    {
        var (tools, store, _) = Make();
        tools.ScheduleMessage("0 8 * * *", "Drink water", "water");
        store.SetStatus("water", ReminderStatus.Paused);

        var result = tools.ResumeScheduled("water");
        Assert.Contains("Resumed", result);
        Assert.Equal(ReminderStatus.Active, store.LoadAll().Reminders.Single().Status);
    }

    [Fact]
    public void Pause_and_resume_report_unknown_ids()
    {
        var (tools, _, _) = Make();
        Assert.Contains("No scheduled item", tools.PauseScheduled("nope"));
        Assert.Contains("No scheduled item", tools.ResumeScheduled("nope"));
    }

    [Fact]
    public void List_shows_active_and_paused_but_not_done()
    {
        var (tools, store, _) = Make();
        tools.ScheduleMessage("0 8 * * *", "Active one", "a");
        tools.ScheduleMessage("0 9 * * *", "Paused one", "b");
        tools.ScheduleMessage("2026-06-15 09:00", "Done one", "c");
        store.SetStatus("b", ReminderStatus.Paused);
        store.SetStatus("c", ReminderStatus.Done);

        var result = tools.ListScheduled();
        Assert.Contains("Active one", result);
        Assert.Contains("Paused one", result);
        Assert.DoesNotContain("Done one", result);
    }
}
