using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Agents;

public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        ILogger<TroubleshootingAgent> logger) : base(conversationId, llmService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateTroubleshootingAgent();

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}