using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Adapters;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;

namespace Morgana.Example.Agents;

[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        (aiAgent, contextProvider) = morganaAgentAdapter.CreateAgent(GetType(), conversationId, OnSharedContextUpdate);
    }
}