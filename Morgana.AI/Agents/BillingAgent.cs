using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Agents;

[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<BillingAgent> logger) : base(conversationId, llmService, promptResolverService, logger)
    {
        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient(), promptResolverService, logger);
        aiAgent = adapter.CreateBillingAgent(AgentContext);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}