using Erda.Agents.Services;
using Erda.Agents.Tools;
using Erda.Agents.WebChat;
using Erda.Agents.Workflows;
using Erda.Core.Abstractions;
using Erda.Core.Services;

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
        services.AddSingleton<NotifyTools>();
        services.AddSingleton<ReminderTools>();
        services.AddSingleton<VaultEditorTool>();
        services.AddSingleton<CardPriceTool>();
        services.AddSingleton<AppleReminderTools>();
        services.AddSingleton<AppleCalendarTools>();

        // The in-process reasoner — the streamed Responses replacement for the old codex subprocess.
        // Lives in this (MAF) layer, not Core, because it builds AIAgents; every former Codex consumer
        // (voice-memo, recipe, error-watch) takes the Erda.Core.Services.IReasoner seam.
        services.AddSingleton<IReasoner, ResponsesReasoner>();

        // Voice-memo workflow, exposed to the WhatsApp channel via IMemoProcessor.
        services.AddSingleton<MemoProcessor>();
        services.AddSingleton<IMemoProcessor>(sp => sp.GetRequiredService<MemoProcessor>());

        // Workflows tab: auto-discover every IWorkflowProvider in this assembly (reflection), so a new
        // workflow appears in the panel just by adding a provider — no registry edit. The catalog
        // builds each and asks MAF's WorkflowVisualizer to diagram it.
        foreach (var providerType in typeof(ServiceCollectionExtensions).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IWorkflowProvider).IsAssignableFrom(t)))
            services.AddSingleton(typeof(IWorkflowProvider), providerType);
        services.AddSingleton<IWorkflowCatalog, WorkflowCatalog>();

        // The long-lived session responder for the WhatsApp conversation.
        services.AddSingleton<IAgentResponder, ErdaAgentResponder>();

        // Web-chat channel: own session + streaming, isolated from the WhatsApp conversation.
        services.AddSingleton<IWebChat, WebChatService>();

        return services;
    }
}
