using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.SessionStores;

/// <summary>
/// The session of <c>MorganaHostedAgent</c>: it carries the conversation an inbound A2A request
/// belongs to and nothing else.
/// </summary>
/// <remarks>
/// A2A identifies a conversation by its <c>contextId</c>, which the hosting layer turns into the key
/// it asks a session under. This session is therefore the only place that identity reaches
/// <c>MorganaHostedAgent</c>, which is handed a session but never the request.
/// </remarks>
public sealed class MorganaHostedAgentSession : AgentSession
{
    /// <summary>Morgana conversation this request belongs to, i.e. the A2A context id.</summary>
    public string ConversationId { get; }

    /// <summary>Binds the session to the conversation named by the inbound request.</summary>
    /// <param name="conversationId">A2A context id, used verbatim as the Morgana conversation id.</param>
    public MorganaHostedAgentSession(string conversationId) => ConversationId = conversationId;
}

/// <summary>
/// The <see cref="AgentSessionStore"/> of <c>MorganaHostedAgent</c>: hands it the A2A context id as
/// a <see cref="MorganaHostedAgentSession"/> and stores nothing.
/// </summary>
/// <remarks>
/// Storing nothing is correct, not a shortcut: a Morgana agent's state lives in its actor and,
/// encrypted, in the per-conversation database. A second store would be a copy that silently diverges.
/// </remarks>
public sealed class MorganaHostedAgentSessionStore : AgentSessionStore
{
    /// <summary>Logger for inbound-request diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>Builds the store over the logger used to trace inbound A2A requests.</summary>
    /// <param name="logger">Logger for inbound-request diagnostics.</param>
    public MorganaHostedAgentSessionStore(ILogger logger) => this.logger = logger;

    /// <inheritdoc />
    public override ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Inbound A2A request for agent '{AgentName}' on conversation '{ConversationId}'", agent.Name, sessionStoreId);

        return ValueTask.FromResult<AgentSession>(new MorganaHostedAgentSession(sessionStoreId));
    }

    /// <inheritdoc />
    public override ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}