using System.Threading.Channels;

namespace Erda.Core.WhatsApp;

/// <summary>
/// A simple in-process queue between the inbound HTTP endpoint (which returns 202 immediately) and
/// the single-consumer worker that actually runs the agent. Single-reader so the owner's messages
/// are processed one at a time, in order (the agent session is not concurrency-safe).
/// </summary>
public sealed class WhatsAppInboundQueue
{
    private readonly Channel<InboundMessage> _channel =
        Channel.CreateUnbounded<InboundMessage>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(InboundMessage message, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(message, cancellationToken);

    public IAsyncEnumerable<InboundMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
