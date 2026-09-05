using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.SessionStores;

/// <summary>
/// The session of <c>MorganaHostedAgent</c>: it carries the conversation an inbound A2A request
/// belongs to and the system that named it.
/// </summary>
/// <remarks>
/// A2A identifies a conversation by its <c>contextId</c>, which the hosting layer turns into the key
/// it asks a session under. This session is therefore the only place that identity reaches
/// <c>MorganaHostedAgent</c>, which is handed a session but never the request.
/// </remarks>
public sealed class MorganaHostedAgentSession : AgentSession
{
    /// <summary>Morgana conversation this request belongs to.</summary>
    public string ConversationId { get; }

    /// <summary>
    /// System that asked, as its token declared it, or <c>null</c> when the request reached this
    /// agent without one — which the gate in front of the endpoint does not allow.
    /// </summary>
    public string? CallerIssuer { get; }

    /// <summary>Binds the session to the conversation named by the inbound request.</summary>
    /// <param name="conversationId">Conversation to serve on, already scoped to whoever named it.</param>
    /// <param name="callerIssuer">System that asked.</param>
    public MorganaHostedAgentSession(string conversationId, string? callerIssuer = null)
    {
        ConversationId = conversationId;
        CallerIssuer = callerIssuer;
    }
}

/// <summary>
/// The <see cref="AgentSessionStore"/> of <c>MorganaHostedAgent</c>: turns a request's A2A context
/// id into the conversation it is served on. It stores nothing.
/// </summary>
/// <remarks>
/// Storing nothing is correct, not a shortcut: a Morgana agent's state lives in its actor and,
/// encrypted, in the per-conversation database. A second store would be a copy that silently diverges.
/// </remarks>
public sealed class MorganaHostedAgentSessionStore : AgentSessionStore
{
    /// <summary>
    /// Parts a partner's name from the context id it wrote. Deliberately a character nobody puts in
    /// an issuer name, so two partners cannot be made to agree on one conversation by choosing their
    /// context ids around it.
    /// </summary>
    private const string ForeignConversationSeparator = "~";

    /// <summary>Names the system that asked, taken from the token the gate already validated.</summary>
    private readonly Func<string?> callerIssuerResolver;

    /// <summary>Logger for inbound-request diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>Builds the store over the means of telling who is asking.</summary>
    /// <param name="callerIssuerResolver">Names the system behind the request being served.</param>
    /// <param name="logger">Logger for inbound-request diagnostics.</param>
    public MorganaHostedAgentSessionStore(Func<string?> callerIssuerResolver, ILogger logger)
    {
        this.callerIssuerResolver = callerIssuerResolver;
        this.logger = logger;
    }

    /// <inheritdoc />
    public override ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        string? callerIssuer = callerIssuerResolver();
        string conversationId = ResolveConversationId(sessionStoreId, callerIssuer);

        logger.LogInformation(
            "Inbound A2A request from '{CallerIssuer}' for agent '{AgentName}' on conversation '{ConversationId}'",
            callerIssuer ?? "an undeclared system", agent.Name, conversationId);

        return ValueTask.FromResult<AgentSession>(new MorganaHostedAgentSession(conversationId, callerIssuer));
    }

    /// <summary>
    /// Decides which conversation an inbound request is served on, which is the whole of what keeps
    /// one caller out of another's.
    /// </summary>
    /// <remarks>
    /// A context id is written by whoever calls. For this installation's own ring that is the point:
    /// a colleague must land on the very conversation the user is having and the id names it. For
    /// anyone else it is a string a stranger chose. Honouring it as a conversation of ours would
    /// let a partner name a live user's — reaching their agents, reading the shared context those
    /// agents were told and spending their budget. So a partner's exchanges are conversations of the
    /// partner: same id, kept apart by the one thing that caller cannot choose.
    /// </remarks>
    /// <param name="sessionStoreId">A2A context id, as the caller wrote it.</param>
    /// <param name="callerIssuer">System that asked.</param>
    private static string ResolveConversationId(string sessionStoreId, string? callerIssuer)
        => callerIssuer is null || string.Equals(callerIssuer, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase)
            ? sessionStoreId
            : $"{callerIssuer}{ForeignConversationSeparator}{sessionStoreId}";

    /// <inheritdoc />
    public override ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
