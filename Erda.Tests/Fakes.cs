using Erda.Core.Abstractions;
using Erda.Core.Data;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Erda.Core.Services.Seq;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

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

    public Task<string> ProcessAsync(string transcript, CancellationToken cancellationToken = default)
    {
        Transcripts.Add(transcript);
        return Task.FromResult(Reply);
    }
}

public sealed class FakeAgentResponder : IAgentResponder
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
    public List<IReadOnlyList<ChatMessage>> RunOnceCalls { get; } = [];
    public int Resets { get; private set; }
    public AgentReply Reply { get; set; } = new("ok", 10, 5, 15, ["consult_codex"]);

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

public sealed class FakeCodexRunner : ICodexRunner
{
    public List<(string Prompt, bool WebSearch)> Calls { get; } = [];
    public string Result { get; set; } = "codex says hi";
    public Exception? Throw { get; set; }

    public Task<string> RunPromptAsync(string prompt, bool enableWebSearch = false,
        CancellationToken cancellationToken = default, string? logLabel = null, string? reasoningEffort = null)
    {
        Calls.Add((prompt, enableWebSearch));
        return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult(Result);
    }
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
    public bool Result { get; set; } = true;

    public Task<bool> SendAsync(string toJid, string text, CancellationToken cancellationToken = default)
    {
        Sent.Add((toJid, text));
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

public sealed class FakeActivityRecorder : IActivityRecorder
{
    public List<(string Kind, string Summary)> Records { get; } = [];

    public void Record(string kind, string summary, object? detail = null) => Records.Add((kind, summary));

    public IReadOnlyList<ActivityEntry> Recent(int max = 100) => [];

    public event Action<ActivityEntry>? Recorded { add { } remove { } }
}
