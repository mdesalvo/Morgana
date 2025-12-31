using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;

namespace Morgana.AI.Agents;

[HandlesIntent("troubleshooting")]
public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<TroubleshootingAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger,
        IMCPToolProvider? mcpToolProvider=null) : base(conversationId, llmService, promptResolverService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient(), promptResolverService, logger, contextProviderLogger, mcpToolProvider);
        
        // Crea agente e context provider
        (aiAgent, contextProvider) = adapter.CreateTroubleshootingAgent(OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}