using Erda.Core.Abstractions;
using Erda.Core.Data;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Core.Services.Seq;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erda.Tests;

public sealed class FakeHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "Erda.Tests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}

public sealed class FakeMemoProcessor : IMemoProcessor
{
    public List<string> Transcripts { get; } = [];
    public string Reply { get; set; } = "Saved voice memo to 1 Inbox/memo.md (10 chars).";
    public string NotePath { get; set; } = "1 Inbox/memo.md";

    public List<string> RawTranscripts { get; } = [];
    public bool ThrowOnProcess { get; set; }

    public Task<MemoResult> ProcessAsync(string transcript, CancellationToken cancellationToken = default)
    {
        Transcripts.Add(transcript);
        if (ThrowOnProcess)
            throw new InvalidOperationException("reasoner unavailable");
        return Task.FromResult(new MemoResult(Reply, NotePath));
    }

    public Task<string> SaveRawAsync(string transcript, CancellationToken cancellationToken = default)
    {
        RawTranscripts.Add(transcript);
        return Task.FromResult("1 Inbox/raw.md");
    }
}

public sealed class FakeVoiceMemoArchive : IVoiceMemoArchive
{
    public long? NextId { get; set; } = 1;
    public List<(long Id, string? NotePath, string Status)> Completed { get; } = [];
    public List<long> Failed { get; } = [];

    public Task<long?> RecordAsync(string displayFileName, string sourceAudioPath, CancellationToken ct = default)
        => Task.FromResult(NextId);

    public Task CompleteAsync(long id, string? notePath, string status, CancellationToken ct = default)
    {
        Completed.Add((id, notePath, status));
        return Task.CompletedTask;
    }

    public Task FailAsync(long id, CancellationToken ct = default)
    {
        Failed.Add(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VoiceMemoView>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VoiceMemoView>>([]);

    public Task<VoiceMemoAudio?> OpenAudioAsync(long id, CancellationToken ct = default)
        => Task.FromResult<VoiceMemoAudio?>(null);

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class FakeAgentResponder : IAgentResponder
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
    public List<IReadOnlyList<ChatMessage>> RunOnceCalls { get; } = [];
    public int Resets { get; private set; }
    public AgentReply Reply { get; set; } = new("ok", 10, 5, 15, []);

    public Task<AgentReply> RespondAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        Calls.Add(messages);
        return Task.FromResult(Reply);
    }

    public Task<AgentReply> RunOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        RunOnceCalls.Add(messages);
        return Task.FromResult(Reply);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Resets++;
        return Task.CompletedTask;
    }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);
}

public sealed class FakeReasoner : IReasoner
{
    public List<(string Prompt, bool WebSearch, string? ReasoningEffort, string? LogLabel)> Calls { get; } = [];
    public string Result { get; set; } = "reasoner says hi";
    public Exception? Throw { get; set; }

    public Task<string> ReasonAsync(string prompt, bool webSearch = false,
        CancellationToken cancellationToken = default, string? logLabel = null, string? reasoningEffort = null)
    {
        Calls.Add((prompt, webSearch, reasoningEffort, logLabel));
        return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult(Result);
    }

    // Mirrors ResponsesReasoner.RunAsync: webSearch ON, the voice-memo log label.
    public Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken cancellationToken = default)
        => ReasonAsync($"{developerInstruction}\n\nTranscript:\n{transcript}",
            webSearch: true, cancellationToken, logLabel: "voice-memo processing");
}

public sealed class FakePreScriptRunner : IPreScriptRunner
{
    public List<string> Scripts { get; } = [];
    public string Output { get; set; } = "CONTEXT";
    public Exception? Throw { get; set; }

    public Task<string> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        Scripts.Add(script);
        return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult(Output);
    }
}

public sealed class FakeUrlFetcher : IUrlFetcher
{
    public List<string> Urls { get; } = [];
    public string Html { get; set; } = "<html><body>hi</body></html>";
    public Exception? Throw { get; set; }

    public Task<string> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        Urls.Add(url);
        return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult(Html);
    }
}

public sealed class FakeWhatsAppSender : IWhatsAppSender
{
    public List<(string To, string Text)> Sent { get; } = [];
    public List<(string To, string FilePath, string? Caption)> SentImages { get; } = [];
    public List<(string To, string State)> Presence { get; } = [];
    public bool Result { get; set; } = true;

    /// <summary>Per-call results consumed before falling back to <see cref="Result"/> (for retry tests).</summary>
    public Queue<bool> ResultQueue { get; } = new();

    public Task<bool> SendAsync(string toJid, string text, CancellationToken cancellationToken = default)
    {
        Sent.Add((toJid, text));
        return Task.FromResult(ResultQueue.Count > 0 ? ResultQueue.Dequeue() : Result);
    }

    public Task SetPresenceAsync(string chatJid, string state, CancellationToken cancellationToken = default)
    {
        Presence.Add((chatJid, state));
        return Task.CompletedTask;
    }

    public Task<bool> SendImageAsync(string toJid, string filePath, string? caption, CancellationToken cancellationToken = default)
    {
        SentImages.Add((toJid, filePath, caption));
        return Task.FromResult(Result);
    }
}

public sealed class FakeTranscriber : ITranscriber
{
    public string Transcript { get; set; } = "hello world";
    public int Calls { get; private set; }

    public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Transcript);
    }
}

public sealed class FakeSeqClient : ISeqClient
{
    public Queue<IReadOnlyList<SeqError>> Responses { get; } = new();
    public List<(string Filter, DateTimeOffset? From)> Queries { get; } = [];

    public Task<IReadOnlyList<SeqError>> QueryErrorsAsync(string filter, DateTimeOffset? fromUtc, int count, CancellationToken cancellationToken = default)
    {
        Queries.Add((filter, fromUtc));
        IReadOnlyList<SeqError> next = Responses.Count > 0 ? Responses.Dequeue() : [];
        return Task.FromResult(next);
    }
}

public sealed class FakeAnalyzer : IErrorAnalyzer
{
    public int Calls { get; private set; }

    public Task<string> AnalyzeAsync(SeqError error, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult("analysis");
    }
}

/// <summary>Captures formatted log messages so a test can assert what did (and did not) get logged.</summary>
public sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

public sealed class FakeActivityRecorder : IActivityRecorder
{
    public List<(string Kind, string Summary, object? Detail)> Records { get; } = [];

    public void Record(string kind, string summary, object? detail = null) => Records.Add((kind, summary, detail));

    public IReadOnlyList<ActivityEntry> Recent(int max = 100) => [];

    public event Action<ActivityEntry>? Recorded { add { } remove { } }
}
