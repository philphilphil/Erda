using Erda.Agents.Tools;
using Erda.Agents.WebChat;
using Erda.Agents.Workflows;
using Erda.Core.Abstractions;

namespace Erda.Agents;

/// <summary>
/// DI wiring for the MAF layer: the agent's tools, the voice-memo workflow, and the long-lived
/// responder that drives the <c>erda</c> agent for the WhatsApp channel. The keyed <c>erda</c>
/// <see cref="Microsoft.Agents.AI.AIAgent"/> itself is registered by the host via
/// <c>builder.AddAIAgent</c> (see <see cref="ErdaAgent.Create"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddErdaAgents(this IServiceCollection services)
    {
        // Tools — the agent's capability surface.
        services.AddSingleton<ObsidianTools>();
        services.AddSingleton<ReasoningTools>();
        services.AddSingleton<NotifyTools>();
        services.AddSingleton<ReminderTools>();

        // Voice-memo workflow, exposed to the WhatsApp channel via IMemoProcessor.
        services.AddSingleton<MemoProcessor>();
        services.AddSingleton<IMemoProcessor>(sp => sp.GetRequiredService<MemoProcessor>());

        // The long-lived session responder for the WhatsApp conversation.
        services.AddSingleton<IAgentResponder, ErdaAgentResponder>();

        // Web-chat channel: own session + streaming, isolated from the WhatsApp conversation.
        services.AddSingleton<IWebChat, WebChatService>();

        return services;
    }
}
