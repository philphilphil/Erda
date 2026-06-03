using Erda.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Erda.Tests;

public class ReminderStoreTests
{
    private static ReminderStore MakeStore() => new(TestDb.NewFactory(), NullLogger<ReminderStore>.Instance);

    /// <summary>Seed the equivalent of the old sample note: an active one-shot reminder, a paused
    /// recurring reminder, and an active recurring prompt.</summary>
    private static ReminderStore Seeded()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");
        store.Append(ReminderKind.Reminder, "trash", "0 19 * * 0", "Take out the trash");
        store.SetStatus("trash", ReminderStatus.Paused);
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?");
        return store;
    }

    [Fact]
    public void Loads_reminders_and_prompts_with_kinds_and_status()
    {
        var load = Seeded().LoadAll();

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
        var store = MakeStore();
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");
        store.Append(ReminderKind.Reminder, "trash", "0 19 * * 0", "Take out the trash");
        store.Append(ReminderKind.Prompt, "weather", "not-a-time", "Weather?");

        var load = store.LoadAll();

        Assert.DoesNotContain(load.Reminders, r => r.Id == "weather");
        Assert.Equal(2, load.Reminders.Count); // call-mom + trash still parse
        Assert.Single(load.Malformed);
        Assert.Contains("not-a-time", load.Malformed[0]);
    }

    [Fact]
    public void Append_adds_a_prompt_row()
    {
        var store = Seeded();
        store.Append(ReminderKind.Prompt, "news", "@daily", "Top headlines?");

        var prompt = store.LoadAll().Reminders.Single(r => r.Id == "news");
        Assert.Equal(ReminderKind.Prompt, prompt.Kind);
        Assert.Equal("Top headlines?", prompt.Text);
    }

    [Fact]
    public void Append_works_on_an_empty_store()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Reminder, "water", "0 10 * * *", "Drink water");

        var r = store.LoadAll().Reminders.Single();
        Assert.Equal("water", r.Id);
        Assert.Equal(ReminderKind.Reminder, r.Kind);
    }

    [Fact]
    public void Append_updates_an_existing_id_in_place()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Reminder, "x", "0 8 * * *", "first");
        store.Append(ReminderKind.Prompt, "x", "0 9 * * *", "second");

        var r = store.LoadAll().Reminders.Single();
        Assert.Equal(ReminderKind.Prompt, r.Kind);
        Assert.Equal("second", r.Text);
        Assert.Equal("0 9 * * *", r.When);
    }

    [Fact]
    public void SetStatus_marks_done()
    {
        var store = Seeded();
        Assert.True(store.SetStatus("call-mom", ReminderStatus.Done));

        Assert.Equal(ReminderStatus.Done, store.LoadAll().Reminders.Single(r => r.Id == "call-mom").Status);
    }

    [Fact]
    public void SetStatus_returns_false_for_unknown_id()
    {
        Assert.False(Seeded().SetStatus("nope", ReminderStatus.Done));
    }

    [Fact]
    public void Remove_deletes_the_row()
    {
        var store = Seeded();
        Assert.True(store.Remove("trash"));

        var load = store.LoadAll();
        Assert.DoesNotContain(load.Reminders, r => r.Id == "trash");
        Assert.Equal(2, load.Reminders.Count);
    }

    [Fact]
    public void Pipe_in_message_round_trips()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Reminder, "piped", "0 8 * * *", "buy milk | eggs | bread");

        Assert.Equal("buy milk | eggs | bread", store.LoadAll().Reminders.Single(r => r.Id == "piped").Text);
    }

    [Fact]
    public void Append_round_trips_direct_to_codex_and_prescript()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Prompt, "news", "0 6 * * *", "Top headlines?",
            directToCodex: true, preScript: "curl x");

        var r = store.LoadAll().Reminders.Single();
        Assert.True(r.DirectToCodex);
        Assert.Equal("curl x", r.PreScript);
    }

    [Fact]
    public void Append_without_extras_loads_defaults()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Prompt, "x", "0 6 * * *", "hi");

        var r = store.LoadAll().Reminders.Single();
        Assert.False(r.DirectToCodex);
        Assert.Null(r.PreScript);
    }

    [Fact]
    public void Update_changes_definition_columns()
    {
        var store = MakeStore();
        store.Append(ReminderKind.Prompt, "news", "0 6 * * *", "Old text");

        Assert.True(store.Update("news", "0 7 * * *", "New text", directToCodex: true, preScript: "echo hi"));

        var r = store.LoadAll().Reminders.Single(r => r.Id == "news");
        Assert.Equal("0 7 * * *", r.When);
        Assert.Equal("New text", r.Text);
        Assert.True(r.DirectToCodex);
        Assert.Equal("echo hi", r.PreScript);
        Assert.Equal(ReminderKind.Prompt, r.Kind); // kind unchanged
    }

    [Fact]
    public void Update_preserves_status_and_run_state()
    {
        var dbf = TestDb.NewFactory();
        var store = new ReminderStore(dbf, NullLogger<ReminderStore>.Instance);
        var stateStore = new ReminderStateStore(dbf);
        store.Append(ReminderKind.Prompt, "news", "0 6 * * *", "Old text");
        store.SetStatus("news", ReminderStatus.Paused);
        var lastFired = new DateTimeOffset(2026, 6, 1, 5, 0, 0, TimeSpan.Zero);
        stateStore.Save(new ReminderState { LastFiredUtc = { ["news"] = lastFired } });

        Assert.True(store.Update("news", "0 7 * * *", "New text", directToCodex: false));

        Assert.Equal(ReminderStatus.Paused, store.LoadAll().Reminders.Single(r => r.Id == "news").Status);
        Assert.Equal(lastFired, stateStore.Load().LastFiredUtc["news"]); // run-state untouched
    }

    [Fact]
    public void Update_returns_false_for_unknown_id()
    {
        Assert.False(MakeStore().Update("nope", "0 6 * * *", "x", directToCodex: false));
    }
}
