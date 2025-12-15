using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Agents;

public class BillingAgent : MorganaAgent
{
    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<BillingAgent> logger) : base(conversationId, llmService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient(), promptResolverService);
        aiAgent = adapter.CreateBillingAgent();

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}