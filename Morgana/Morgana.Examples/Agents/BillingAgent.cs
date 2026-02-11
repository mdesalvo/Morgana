using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Adapters;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;

namespace Morgana.Examples.Agents;

[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService conversationPersistenceService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, configuration)
    {
        (aiAgent, aiContextProvider) = morganaAgentAdapter.CreateAgent(GetType(), conversationId, OnSharedContextUpdate);
    }
}