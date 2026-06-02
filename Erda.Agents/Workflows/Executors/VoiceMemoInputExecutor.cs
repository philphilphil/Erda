using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Workflows.Executors;

/// <summary>
/// Entry adapter for the voice-memo workflow.
///
/// When a workflow is hosted as an AIAgent (DevUI / OpenAI-Responses surface), its start
/// executor MUST speak the chat protocol: it has to accept <c>List&lt;ChatMessage&gt;</c> + a
/// <c>TurnToken</c>. A plain <c>Executor&lt;string, string&gt;</c> start node fails validation
/// with "Workflow does not support ChatProtocol".
///
/// This adapter subclasses <see cref="ChatProtocolExecutor"/> to accept the chat input, pulls
/// the .m4a path out of the user's message text, and forwards it as a plain <c>string</c> to
/// the first real step (<see cref="TranscribeExecutor"/>). The rest of the chain stays simple.
/// </summary>
internal sealed class VoiceMemoInputExecutor() : ChatProtocolExecutor("voice-memo-input")
{
    // Declare that, in addition to the chat protocol, this executor also emits a plain string
    // (the path) to the next executor.
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        => base.ConfigureProtocol(protocolBuilder).SendsMessage<string>();

    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken)
    {
        var path = (messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text
                    ?? messages.LastOrDefault()?.Text
                    ?? string.Empty).Trim();

        await context.SendMessageAsync(path, cancellationToken: cancellationToken);
    }
}
