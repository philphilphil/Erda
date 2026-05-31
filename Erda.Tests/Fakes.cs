using Erda.Agents;
using Erda.Scheduling;
using Erda.Services;
using Erda.Services.Seq;
using Erda.WhatsApp;
using Microsoft.Extensions.AI;

namespace Erda.Tests;

public sealed class FakeAgentResponder : IAgentResponder
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
    public AgentReply Reply { get; set; } = new("ok", 10, 5, 15, ["consult_codex"]);

    public Task<AgentReply> RespondAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        Calls.Add(messages);
        return Task.FromResult(Reply);
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
