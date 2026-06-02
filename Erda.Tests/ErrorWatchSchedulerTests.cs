using Erda.Configuration;
using Erda.Scheduling;
using Erda.Services.Seq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class ErrorWatchSchedulerTests
{
    private const string OwnerJid = "4915123456789@s.whatsapp.net";

    private static SeqError Err(string id, string template, string level = "Error", DateTimeOffset? ts = null) =>
        new()
        {
            Id = id,
            Level = level,
            MessageTemplate = template,
            RenderedMessage = template,
            Timestamp = ts ?? DateTimeOffset.UtcNow,
        };

    private static (ErrorWatchScheduler Scheduler, FakeSeqClient Seq, FakeAnalyzer Analyzer, FakeWhatsAppSender Sender) Make()
    {
        var seq = new FakeSeqClient();
        var analyzer = new FakeAnalyzer();
        var sender = new FakeWhatsAppSender();
        var scheduler = new ErrorWatchScheduler(
            Options.Create(new ErrorWatchOptions()),
            Options.Create(new SeqOptions { ServerUrl = "http://seq" }),
            Options.Create(new WhatsAppOptions { OwnerNumber = "+49 151 2345 6789" }),
            seq, analyzer, sender, TempStore(), new FakeActivityRecorder(), NullLogger<ErrorWatchScheduler>.Instance);
        return (scheduler, seq, analyzer, sender);
    }

    private static ErrorWatchStateStore TempStore() => new(TestDb.NewFactory());

    private static ErrorWatchState StateFrom(int minutesAgo = 10) =>
        new() { LastTimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo) };

    [Fact]
    public async Task New_errors_alert_once_per_signature_then_dedup()
    {
        var (scheduler, seq, analyzer, sender) = Make();
        var opts = new ErrorWatchOptions { AnalyzeWithCodex = true, MaxAlertsPerPoll = 5 };
        var store = TempStore();
        var state = StateFrom();

        // e3 shares e1's signature (same template) -> only two new signatures.
        seq.Responses.Enqueue([Err("e1", "A {x}"), Err("e2", "B {y}"), Err("e3", "A {x}")]);
        await scheduler.PollOnceAsync(opts, store, state, OwnerJid, default);

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(2, analyzer.Calls);

        // Same events returned next poll -> already seen by id -> nothing new.
        seq.Responses.Enqueue([Err("e1", "A {x}"), Err("e2", "B {y}")]);
        await scheduler.PollOnceAsync(opts, store, state, OwnerJid, default);

        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task Respects_MaxAlertsPerPoll_and_sends_a_summary()
    {
        var (scheduler, _, analyzer, sender) = Make();
        var seq = new FakeSeqClient();
        var schedulerWithSeq = new ErrorWatchScheduler(
            Options.Create(new ErrorWatchOptions()),
            Options.Create(new SeqOptions { ServerUrl = "http://seq" }),
            Options.Create(new WhatsAppOptions { OwnerNumber = "+49 151 2345 6789" }),
            seq, analyzer, sender, TempStore(), new FakeActivityRecorder(), NullLogger<ErrorWatchScheduler>.Instance);

        var opts = new ErrorWatchOptions { AnalyzeWithCodex = false, MaxAlertsPerPoll = 2 };
        seq.Responses.Enqueue([Err("e1", "A"), Err("e2", "B"), Err("e3", "C"), Err("e4", "D")]);

        await schedulerWithSeq.PollOnceAsync(opts, TempStore(), StateFrom(), OwnerJid, default);

        // 2 alerts + 1 "more suppressed" summary, no Codex calls.
        Assert.Equal(3, sender.Sent.Count);
        Assert.Equal(0, analyzer.Calls);
        Assert.Contains("more new error", sender.Sent[^1].Text);
    }

    [Fact]
    public async Task Watermark_advances_to_newest_event()
    {
        var (scheduler, seq, _, _) = Make();
        var opts = new ErrorWatchOptions { AnalyzeWithCodex = false };
        var state = StateFrom();
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-1);
        seq.Responses.Enqueue([Err("e1", "A", ts: t1), Err("e2", "B", ts: t2)]);

        await scheduler.PollOnceAsync(opts, TempStore(), state, OwnerJid, default);

        Assert.Equal(t2, state.LastTimestampUtc);
    }

    [Fact]
    public async Task No_events_is_a_noop()
    {
        var (scheduler, _, _, sender) = Make();
        await scheduler.PollOnceAsync(new ErrorWatchOptions(), TempStore(), StateFrom(), OwnerJid, default);
        Assert.Empty(sender.Sent);
    }
}
