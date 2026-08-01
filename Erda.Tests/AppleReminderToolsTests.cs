using System.Text.Json;
using Erda.Agents.Tools;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class AppleReminderToolsTests
{
    // The fake lives in FakeAppleBridgeClient.cs, shared with AppleCalendarToolsTests — the two tool
    // classes talk to one client, and two copies would drift.
    private static AppleReminderTools Make(FakeAppleBridgeClient client) => new(client);

    private static AIFunction Tool(AppleReminderTools tools, string name) =>
        (AIFunction)tools.AsTools().Single(t => ((AIFunction)t).Name == name);

    [Fact]
    public void Exposes_the_three_apple_reminder_tools()
    {
        var names = Make(new FakeAppleBridgeClient()).AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Contains("create_apple_reminder", names);
        Assert.Contains("list_apple_reminders", names);
        Assert.Contains("complete_apple_reminder", names);
    }

    [Fact]
    public async Task Create_reminder_forwards_arguments_and_reports_success()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateResult = AppleBridgeResult<AppleReminder>.Ok(
                new AppleReminder("rem_1", "Groceries", "Buy milk", null, null, 0, false, null)),
        };

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["list"] = "Groceries", ["title"] = "Buy milk" }))!).GetString()!;

        Assert.Contains("Buy milk", result);
        Assert.Contains("Groceries", result);
        Assert.Equal(("Groceries", "Buy milk", (string?)null, (DateTimeOffset?)null, (int?)null), fake.CreateCall);
    }

    [Fact]
    public async Task Create_reminder_relays_the_bridge_error_message()
    {
        var fake = new FakeAppleBridgeClient { CreateResult = AppleBridgeResult<AppleReminder>.Fail("the list isn't set up") };

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["list"] = "Nope", ["title"] = "x" }))!).GetString()!;

        Assert.Contains("the list isn't set up", result);
    }

    [Fact]
    public async Task Create_reminder_refuses_a_blank_list_without_calling_the_client()
    {
        var fake = new FakeAppleBridgeClient();

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["list"] = "", ["title"] = "x" }))!).GetString()!;

        Assert.Contains("which Reminders list", result);
        Assert.Null(fake.CreateCall);
    }

    [Fact]
    public async Task Create_reminder_refuses_a_blank_title_without_calling_the_client()
    {
        var fake = new FakeAppleBridgeClient();

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["list"] = "Groceries", ["title"] = "  " }))!).GetString()!;

        Assert.Contains("no title", result);
        Assert.Null(fake.CreateCall);
    }

    [Fact]
    public async Task List_reminders_formats_each_item_with_id_title_and_list()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListResult = AppleBridgeResult<IReadOnlyList<AppleReminder>>.Ok(
                [new AppleReminder("rem_1", "Groceries", "Buy milk", null, null, 0, false, null)]),
        };

        var result = ((JsonElement)(await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new()))!).GetString()!;

        Assert.Contains("rem_1", result);
        Assert.Contains("Buy milk", result);
        Assert.Contains("Groceries", result);
    }

    [Fact]
    public async Task List_reminders_passes_a_single_list_filter_through()
    {
        var fake = new FakeAppleBridgeClient();

        await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new() { ["list"] = "Work" });

        Assert.NotNull(fake.ListCall);
        Assert.Equal(["Work"], fake.ListCall!.Value.Lists);
    }

    /// <summary>Omitting the list is how the model asks for everything — it must not become a
    /// filter on the empty string, which the bridge would reject.</summary>
    [Fact]
    public async Task List_reminders_with_no_list_asks_for_every_list()
    {
        var fake = new FakeAppleBridgeClient();

        await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new());

        Assert.NotNull(fake.ListCall);
        Assert.Null(fake.ListCall!.Value.Lists);
    }

    [Fact]
    public async Task List_reminders_reports_when_there_are_none()
    {
        var fake = new FakeAppleBridgeClient { ListResult = AppleBridgeResult<IReadOnlyList<AppleReminder>>.Ok(Array.Empty<AppleReminder>()) };

        var result = ((JsonElement)(await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new()))!).GetString()!;

        Assert.Contains("No Apple Reminders", result);
    }

    [Fact]
    public async Task List_reminders_relays_the_bridge_error_message()
    {
        var fake = new FakeAppleBridgeClient { ListResult = AppleBridgeResult<IReadOnlyList<AppleReminder>>.Fail("bridge unreachable") };

        var result = ((JsonElement)(await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new()))!).GetString()!;

        Assert.Contains("bridge unreachable", result);
    }

    [Fact]
    public async Task Complete_reminder_reports_already_completed_as_a_no_op()
    {
        var fake = new FakeAppleBridgeClient
        {
            CompleteResult = AppleBridgeResult<AppleReminderCompletion>.Ok(new AppleReminderCompletion("rem_1", true)),
        };

        var result = ((JsonElement)(await Tool(Make(fake), "complete_apple_reminder")
            .InvokeAsync(new() { ["id"] = "rem_1" }))!).GetString()!;

        Assert.Contains("already completed", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("rem_1", fake.CompleteCall);
    }

    [Fact]
    public async Task Complete_reminder_reports_success_when_freshly_completed()
    {
        var fake = new FakeAppleBridgeClient
        {
            CompleteResult = AppleBridgeResult<AppleReminderCompletion>.Ok(new AppleReminderCompletion("rem_1", false)),
        };

        var result = ((JsonElement)(await Tool(Make(fake), "complete_apple_reminder")
            .InvokeAsync(new() { ["id"] = "rem_1" }))!).GetString()!;

        Assert.Contains("complete", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Complete_reminder_refuses_a_blank_id_without_calling_the_client()
    {
        var fake = new FakeAppleBridgeClient();

        var result = ((JsonElement)(await Tool(Make(fake), "complete_apple_reminder")
            .InvokeAsync(new() { ["id"] = "" }))!).GetString()!;

        Assert.Contains("which reminder", result);
        Assert.Null(fake.CompleteCall);
    }
}
