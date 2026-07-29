using Erda.Core.Abstractions;
using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for <see cref="ReminderDispatcher"/> — the shared execute-and-deliver path used by both the
/// scheduler tick and the panel's "run now" endpoint. Covers the manual flag, delivery, and the fact
/// that the dispatcher has no way to touch schedule state (it isn't wired to any state/status store).
/// </summary>
public class ReminderDispatcherTests
{
    private const string Owner = "4915123456789@s.whatsapp.net";

    private static (ReminderDispatcher Dispatcher, FakeAgentResponder Responder, FakeWhatsAppSender Sender, FakeActivityRecorder Recorder)
        Make(bool preScriptEnabled = true)
    {
        var opts = Options.Create(new ReminderOptions { TimeZone = "Europe/Berlin", PreScriptEnabled = preScriptEnabled });
        var vaultDir = Path.Combine(Path.GetTempPath(), "erda-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(Options.Create(new ErdaOptions { VaultPath = vaultDir }));
        var responder = new FakeAgentResponder();
        var sender = new FakeWhatsAppSender();
        var recorder = new FakeActivityRecorder();
        var dispatcher = new ReminderDispatcher(responder, sender, new FakePreScriptRunner(), vault,
            new CurrentTimeContext(new FakeClock(), opts), recorder, opts, NullLogger<ReminderDispatcher>.Instance);
        return (dispatcher, responder, sender, recorder);
    }

    private static Reminder Prompt(string id, string text)
    {
        WhenSpec.TryParse("0 6 * * *", out var spec);
        return new Reminder(id, ReminderKind.Prompt, "0 6 * * *", text, ReminderStatus.Active, spec!);
    }

    [Fact]
    public async Task Manual_prompt_run_invokes_agent_and_delivers_reply_tagged_manual()
    {
        var (d, responder, sender, recorder) = Make();

        var delivered = await d.DispatchAsync(Prompt("news", "What's up?"), Owner, manual: true, default);

        Assert.True(delivered);
        Assert.Single(responder.RunOnceCalls);                          // the agent actually ran
        var sent = Assert.Single(sender.Sent);
        Assert.StartsWith("⏰", sent.Text);                             // reply delivered on WhatsApp
        Assert.Contains("(manual)", Assert.Single(recorder.Records).Summary);
    }

    [Fact]
    public async Task Scheduled_prompt_run_is_not_tagged_manual()
    {
        var (d, _, _, recorder) = Make();

        await d.DispatchAsync(Prompt("news", "What's up?"), Owner, manual: false, default);

        Assert.DoesNotContain("(manual)", Assert.Single(recorder.Records).Summary);
    }

    [Fact]
    public async Task Verbatim_reminder_is_sent_as_is_without_running_the_agent()
    {
        var (d, responder, sender, _) = Make();
        WhenSpec.TryParse("2026-06-15 09:00", out var spec);
        var reminder = new Reminder("call", ReminderKind.Reminder, "2026-06-15 09:00", "Call mom",
            ReminderStatus.Active, spec!);

        var delivered = await d.DispatchAsync(reminder, Owner, manual: true, default);

        Assert.True(delivered);
        Assert.Empty(responder.RunOnceCalls);                          // verbatim → no model call
        Assert.Equal("Call mom", Assert.Single(sender.Sent).Text);      // sent as-is, no ⏰ prefix
    }

    [Fact]
    public async Task Upstream_model_failure_is_reported_instead_of_no_response()
    {
        var (d, responder, sender, recorder) = Make();
        responder.Reply = new AgentReply("", null, null, null, []);   // empty, no usage, no tools

        var delivered = await d.DispatchAsync(Prompt("news", "What's up?"), Owner, manual: false, default);

        Assert.True(delivered);                                       // still sent and still recorded
        var sent = Assert.Single(sender.Sent);
        Assert.Contains("may be overloaded", sent.Text);
        Assert.DoesNotContain("(no response)", sent.Text);
        Assert.StartsWith("⏰", sent.Text);
        Assert.Single(recorder.Records);
    }

    [Fact]
    public async Task Empty_reply_with_usage_still_falls_back_to_no_response()
    {
        var (d, responder, sender, _) = Make();
        responder.Reply = new AgentReply("", 10, 0, 10, []);          // the model answered — with nothing

        await d.DispatchAsync(Prompt("news", "What's up?"), Owner, manual: false, default);

        Assert.Equal("⏰ (no response)", Assert.Single(sender.Sent).Text);
    }

    [Fact]
    public async Task No_owner_configured_neither_runs_nor_sends()
    {
        var (d, responder, sender, _) = Make();

        var delivered = await d.DispatchAsync(Prompt("news", "x"), "", manual: true, default);

        Assert.False(delivered);
        Assert.Empty(responder.RunOnceCalls);
        Assert.Empty(sender.Sent);
    }
}
