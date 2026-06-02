using Erda.Configuration;
using Erda.Services;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Erda.Workflows.Executors;

/// <summary>
/// Step 3 (terminal): Markdown note -> written into the vault; returns a confirmation.
///
/// Returns a <see cref="ChatMessage"/> rather than a plain string: when the workflow is hosted
/// as an AIAgent, only chat-protocol output (a ChatMessage) surfaces as the agent's response
/// text. A string-returning terminal still runs the write (the note is saved) but produces an
/// empty response, so the user would see no confirmation. This makes the confirmation visible.
/// </summary>
internal sealed class ObsidianWriteExecutor(VaultService vault, ErdaOptions options)
    : Executor<string, ChatMessage>("obsidian-write")
{
    public override ValueTask<ChatMessage> HandleAsync(
        string note, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var confirmation = VoiceMemoWriter.Write(vault, options, note);
        return new ValueTask<ChatMessage>(new ChatMessage(ChatRole.Assistant, confirmation));
    }
}
