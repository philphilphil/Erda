using Erda.Configuration;
using Erda.Scheduling;
using Erda.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class ReminderStoreTests
{
    private const string Seed = """
        # Erda Reminders

        ## Reminders
        Sent verbatim.

        | id       | when             | message            | status |
        |----------|------------------|--------------------|--------|
        | call-mom | 2026-06-15 09:00 | Call mom           | active |
        | trash    | 0 19 * * 0       | Take out the trash | paused |

        ## Scheduled prompts
        Run through Erda.

        | id      | when      | prompt   | status |
        |---------|-----------|----------|--------|
        | weather | 0 6 * * * | Weather? | active |
        """;

    private static ReminderStore MakeStore(string? seed = null)
    {
        var vaultDir = Path.Combine(Path.GetTempPath(), "erda-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(Options.Create(new ErdaOptions { VaultPath = vaultDir }));
        if (seed is not null)
            vault.WriteNote("Reminders.md", seed);
        var opts = Options.Create(new ReminderOptions { NotePath = "Reminders.md" });
        return new ReminderStore(vault, opts, NullLogger<ReminderStore>.Instance);
    }

    [Fact]
    public void Loads_reminders_and_prompts_with_kinds_and_status()
    {
        var load = MakeStore(Seed).LoadAll();

        Assert.Empty(load.Malformed);
        Assert.Equal(3, load.Reminders.Count);

        var callMom = load.Reminders.Single(r => r.Id == "call-mom");
        Assert.Equal(ReminderKind.Reminder, callMom.Kind);
        Assert.Equal(ReminderStatus.Active, callMom.Status);
        Assert.False(callMom.Spec.IsRecurring);

        Assert.Equal(ReminderStatus.Paused, load.Reminders.Single(r => r.Id == "trash").Status);

        var weather = load.Reminders.Single(r => r.Id == "weather");
        Assert.Equal(ReminderKind.Prompt, weather.Kind);
        Assert.True(weather.Spec.IsRecurring);
    }

    [Fact]
    public void Skips_and_reports_malformed_when()
    {
        var seed = Seed.Replace("| 0 6 * * * |", "| not-a-time |");
        var load = MakeStore(seed).LoadAll();

        Assert.DoesNotContain(load.Reminders, r => r.Id == "weather");
        Assert.Equal(2, load.Reminders.Count); // call-mom + trash still parse
        Assert.Single(load.Malformed);
        Assert.Contains("not-a-time", load.Malformed[0]);
    }

    [Fact]
    public void Derives_stable_id_for_blank_id_cell()
    {
        var seed = """
            ## Reminders

            | id | when             | message  | status |
            |----|------------------|----------|--------|
            |    | 2026-06-15 09:00 | Call mom | active |
            """;
        var store = MakeStore(seed);

        var id1 = store.LoadAll().Reminders.Single().Id;
        var id2 = store.LoadAll().Reminders.Single().Id;

        Assert.False(string.IsNullOrWhiteSpace(id1));
        Assert.Equal(id1, id2); // deterministic across loads
    }

    [Fact]
    public void Append_adds_row_under_correct_section()
    {
        var store = MakeStore(Seed);
        store.Append(ReminderKind.Prompt, "news", "@daily", "Top headlines?");

        var prompt = store.LoadAll().Reminders.Single(r => r.Id == "news");
        Assert.Equal(ReminderKind.Prompt, prompt.Kind);
        Assert.Equal("Top headlines?", prompt.Text);
    }

    [Fact]
    public void Append_scaffolds_a_missing_note()
    {
        var store = MakeStore(seed: null);
        store.Append(ReminderKind.Reminder, "water", "0 10 * * *", "Drink water");

        var r = store.LoadAll().Reminders.Single();
        Assert.Equal("water", r.Id);
        Assert.Equal(ReminderKind.Reminder, r.Kind);
    }

    [Fact]
    public void SetStatus_marks_done()
    {
        var store = MakeStore(Seed);
        Assert.True(store.SetStatus("call-mom", ReminderStatus.Done));

        Assert.Equal(ReminderStatus.Done, store.LoadAll().Reminders.Single(r => r.Id == "call-mom").Status);
    }

    [Fact]
    public void SetStatus_returns_false_for_unknown_id()
    {
        Assert.False(MakeStore(Seed).SetStatus("nope", ReminderStatus.Done));
    }

    [Fact]
    public void Remove_deletes_the_row()
    {
        var store = MakeStore(Seed);
        Assert.True(store.Remove("trash"));

        var load = store.LoadAll();
        Assert.DoesNotContain(load.Reminders, r => r.Id == "trash");
        Assert.Equal(2, load.Reminders.Count);
    }

    [Fact]
    public void Pipe_in_message_round_trips()
    {
        var store = MakeStore(Seed);
        store.Append(ReminderKind.Reminder, "piped", "0 8 * * *", "buy milk | eggs | bread");

        Assert.Equal("buy milk | eggs | bread", store.LoadAll().Reminders.Single(r => r.Id == "piped").Text);
    }
}
