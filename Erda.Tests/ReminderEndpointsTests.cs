using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Server.Api;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for the create/update reminder endpoint handlers (the internal static methods the minimal-API
/// lambdas delegate to), exercising validation and the new Codex-direct / pre-script fields.
/// </summary>
public class ReminderEndpointsTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);
    private static IOptions<ReminderOptions> Opts() => Options.Create(new ReminderOptions { TimeZone = "Europe/Berlin" });
    private static IClock Clock() => new FakeClock { UtcNow = Now };

    private static ReminderStore EmptyStore() => new(TestDb.NewFactory(), NullLogger<ReminderStore>.Instance);

    private static ReminderStore SeededPromptStore()
    {
        var store = EmptyStore();
        store.Append(ReminderKind.Prompt, "news", "0 6 * * *", "Old");
        return store;
    }

    // ---- PUT /reminders/{id} ----

    [Fact]
    public void Update_unknown_id_returns_404()
    {
        var result = ReminderEndpoints.UpdateReminder(
            "nope", new UpdateReminderRequest("0 7 * * *", "New"), SeededPromptStore(), Clock(), Opts());
        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public void Update_blank_text_returns_400()
    {
        var result = ReminderEndpoints.UpdateReminder(
            "news", new UpdateReminderRequest("0 7 * * *", "   "), SeededPromptStore(), Clock(), Opts());
        Assert.IsType<BadRequest<ErrorResponse>>(result);
    }

    [Fact]
    public void Update_bad_cron_returns_400()
    {
        var result = ReminderEndpoints.UpdateReminder(
            "news", new UpdateReminderRequest("not-a-time", "New"), SeededPromptStore(), Clock(), Opts());
        Assert.IsType<BadRequest<ErrorResponse>>(result);
    }

    [Fact]
    public void Update_happy_path_returns_updated_dto()
    {
        var store = SeededPromptStore();
        var result = ReminderEndpoints.UpdateReminder(
            "news", new UpdateReminderRequest("0 7 * * *", "New", true, "echo hi"), store, Clock(), Opts());

        var ok = Assert.IsType<Ok<ReminderDto>>(result);
        Assert.NotNull(ok.Value);
        Assert.Equal("0 7 * * *", ok.Value!.When);
        Assert.Equal("New", ok.Value.Text);
        Assert.True(ok.Value.DirectToCodex);
        Assert.Equal("echo hi", ok.Value.PreScript);
        Assert.Equal("Prompt", ok.Value.Kind); // kind preserved
    }

    // ---- POST /reminders ----

    [Fact]
    public void Create_prompt_persists_codex_and_prescript()
    {
        var store = EmptyStore();
        var result = ReminderEndpoints.CreateReminder(
            new CreateReminderRequest("Prompt", "0 6 * * *", "Daily news", true, "curl x"), store, Clock(), Opts());

        var ok = Assert.IsType<Ok<ReminderDto>>(result);
        Assert.True(ok.Value!.DirectToCodex);
        Assert.Equal("curl x", ok.Value.PreScript);

        var row = store.LoadAll().Reminders.Single();
        Assert.True(row.DirectToCodex);
        Assert.Equal("curl x", row.PreScript);
    }

    [Fact]
    public void Create_verbatim_reminder_ignores_prompt_only_fields()
    {
        var store = EmptyStore();
        var result = ReminderEndpoints.CreateReminder(
            new CreateReminderRequest("Reminder", "2026-06-15 09:00", "Call mom", true, "curl x"), store, Clock(), Opts());

        var ok = Assert.IsType<Ok<ReminderDto>>(result);
        Assert.False(ok.Value!.DirectToCodex);
        Assert.Null(ok.Value.PreScript);
    }
}
