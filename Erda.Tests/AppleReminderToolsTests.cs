using System.Text.Json;
using Erda.Agents.Tools;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class AppleReminderToolsTests
{
    private sealed class FakeAppleBridgeClient : IAppleBridgeClient
    {
        public AppleBridgeResult<AppleBridgeStatus> StatusResult { get; set; } =
            AppleBridgeResult<AppleBridgeStatus>.Ok(new AppleBridgeStatus("ok", [], []));
        public AppleBridgeResult<AppleReminder> CreateResult { get; set; } =
            AppleBridgeResult<AppleReminder>.Fail("not configured for this test");
        public AppleBridgeResult<IReadOnlyList<AppleReminder>> ListResult { get; set; } =
            AppleBridgeResult<IReadOnlyList<AppleReminder>>.Ok(Array.Empty<AppleReminder>());
        public AppleBridgeResult<AppleReminderCompletion> CompleteResult { get; set; } =
            AppleBridgeResult<AppleReminderCompletion>.Fail("not configured for this test");

        public (string Alias, string Title, string? Notes, DateTimeOffset? DueAt, int? Priority)? CreateCall { get; private set; }
        public (IReadOnlyList<string>? Aliases, int? Limit)? ListCall { get; private set; }
        public string? CompleteCall { get; private set; }

        public Task<AppleBridgeResult<AppleBridgeStatus>> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StatusResult);

        public Task<AppleBridgeResult<AppleReminder>> CreateReminderAsync(
            string alias, string title, string? notes = null, DateTimeOffset? dueAt = null, int? priority = null,
            CancellationToken cancellationToken = default)
        {
            CreateCall = (alias, title, notes, dueAt, priority);
            return Task.FromResult(CreateResult);
        }

        public Task<AppleBridgeResult<IReadOnlyList<AppleReminder>>> ListRemindersAsync(
            IReadOnlyList<string>? aliases = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            ListCall = (aliases, limit);
            return Task.FromResult(ListResult);
        }

        public Task<AppleBridgeResult<AppleReminderCompletion>> CompleteReminderAsync(
            string reminderId, CancellationToken cancellationToken = default)
        {
            CompleteCall = reminderId;
            return Task.FromResult(CompleteResult);
        }
    }

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
                new AppleReminder("rem_1", "groceries", "Buy milk", null, null, 0, false, null)),
        };

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["alias"] = "groceries", ["title"] = "Buy milk" }))!).GetString()!;

        Assert.Contains("Buy milk", result);
        Assert.Contains("groceries", result);
        Assert.Equal(("groceries", "Buy milk", (string?)null, (DateTimeOffset?)null, (int?)null), fake.CreateCall);
    }

    [Fact]
    public async Task Create_reminder_relays_the_bridge_error_message()
    {
        var fake = new FakeAppleBridgeClient { CreateResult = AppleBridgeResult<AppleReminder>.Fail("the list isn't set up") };

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["alias"] = "nope", ["title"] = "x" }))!).GetString()!;

        Assert.Contains("the list isn't set up", result);
    }

    [Fact]
    public async Task Create_reminder_refuses_a_blank_alias_without_calling_the_client()
    {
        var fake = new FakeAppleBridgeClient();

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["alias"] = "", ["title"] = "x" }))!).GetString()!;

        Assert.Contains("which Reminders list", result);
        Assert.Null(fake.CreateCall);
    }

    [Fact]
    public async Task Create_reminder_refuses_a_blank_title_without_calling_the_client()
    {
        var fake = new FakeAppleBridgeClient();

        var result = ((JsonElement)(await Tool(Make(fake), "create_apple_reminder")
            .InvokeAsync(new() { ["alias"] = "groceries", ["title"] = "  " }))!).GetString()!;

        Assert.Contains("no title", result);
        Assert.Null(fake.CreateCall);
    }

    [Fact]
    public async Task List_reminders_formats_each_item_with_id_title_and_alias()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListResult = AppleBridgeResult<IReadOnlyList<AppleReminder>>.Ok(
                [new AppleReminder("rem_1", "groceries", "Buy milk", null, null, 0, false, null)]),
        };

        var result = ((JsonElement)(await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new()))!).GetString()!;

        Assert.Contains("rem_1", result);
        Assert.Contains("Buy milk", result);
        Assert.Contains("groceries", result);
    }

    [Fact]
    public async Task List_reminders_passes_a_single_alias_filter_through()
    {
        var fake = new FakeAppleBridgeClient();

        await Tool(Make(fake), "list_apple_reminders").InvokeAsync(new() { ["alias"] = "work" });

        Assert.NotNull(fake.ListCall);
        Assert.Equal(["work"], fake.ListCall!.Value.Aliases);
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
