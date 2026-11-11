using Morgana.AI.Attributes;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Judges what reaches Morgana's models from outside: the user's message, tool results, partners' questions and what MCP servers and partners offer.
/// Every door fails closed: a text that cannot be judged is refused whoever wrote it, so a guard that cannot answer never admits
/// what it was asked to judge. An offer that cannot be judged is refused without being remembered, so the next admission asks again.
/// </summary>
public interface IGuardRailService
{
    /// <summary>Judges the user's message beside the last answer this process delivered to the conversation; alone when there is none.</summary>
    /// <param name="conversationId">The conversation the message belongs to.</param>
    /// <param name="message">The user's message.</param>
    /// <param name="cancellationToken">Stops the judgment once whoever asked for it stops waiting.</param>
    /// <returns>Whether the message complies and, when it does not, the rule it violates.</returns>
    Task<Records.GuardRailResult> CheckUserMessageAsync(string conversationId, string message, CancellationToken cancellationToken = default);

    /// <summary>Judges a tool result before the agent's model reads it, withholding it when it is not compliant; never throws.</summary>
    /// <param name="conversationId">The conversation the call belongs to.</param>
    /// <param name="toolName">The function the model called.</param>
    /// <param name="toolArguments">The call's arguments as a JSON object, read by the inspector alone as the call the result is judged against.</param>
    /// <param name="toolResult">The result serialized as JSON, as the tool loop hands it to the provider.</param>
    /// <param name="cancellationToken">Stops the judgment once whoever asked for it stops waiting.</param>
    /// <returns>Non-compliant when the result is withheld, with the finding in <c>Violation</c> for an operator's log.</returns>
    Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolArguments, string toolResult, CancellationToken cancellationToken = default);

    /// <summary>Judges a partner's question to an agent of this installation before the agent reads it; never throws.</summary>
    /// <param name="conversationId">The conversation the question is served on, for correlation and charging.</param>
    /// <param name="partnerName">The partner asking, as the authentication gate proved it.</param>
    /// <param name="agentIntent">The agent asked.</param>
    /// <param name="agentTerritory">What the asked agent answers for, as its card publishes it.</param>
    /// <param name="question">The partner's question in its own words.</param>
    /// <param name="cancellationToken">Stops the judgment once whoever asked for it stops waiting.</param>
    /// <returns>Non-compliant when the question is declined, with the finding in <c>Violation</c> for an operator's log.</returns>
    Task<Records.GuardRailResult> CheckPeerQuestionAsync(string conversationId, string partnerName, string agentIntent, string agentTerritory, string question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists an MCP server and admits its tools to the agents that declare it; never throws.
    /// A listing identical to the one approved before is admitted without a model call and any other is examined once however many
    /// callers ask together. A server that cannot be listed yields no tools and a FailClosed verdict that is not remembered, while an
    /// earlier approval stands, because a session the server dropped says nothing about its offer.
    /// </summary>
    /// <param name="serverAttribute">The server declaration carried by the agent class.</param>
    /// <returns>The server's tools when its offer is admitted and none otherwise, with the verdict that says which.</returns>
    Task<Records.MCPServerAdmission> AdmitMCPServerToolsAsync(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Examines the description that a partner's card gives a colleague before the colleague is offered to an agent; never throws.
    /// Remembered like an MCP server's offer: an unchanged description is admitted without a model call and one that cannot be judged is not remembered.
    /// </summary>
    /// <param name="peer">The colleague the card describes.</param>
    /// <param name="functionName">The name under which the model would call the colleague.</param>
    /// <param name="cardDescription">The description the card carries; null when it carries none.</param>
    /// <returns>Non-compliant when the colleague is kept from the agent.</returns>
    Task<Records.GuardRailResult> AdmitPartnerCardAsync(Records.PeerReference peer, string functionName, string? cardDescription);

    /// <summary>Tells whether Morgana holds a completed approval of the server's current offer, false while none was given, a changed one is examined or after a refusal.</summary>
    /// <param name="serverAttribute">The server declaration carried by the agent class.</param>
    bool IsServerAvailableAndSecure(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Tells whether Morgana holds a completed approval of a partner's colleague, false while none was given, a changed card is examined or after a refusal.
    /// Asked of a partner's colleague only: a colleague of this installation is never examined, so nothing is held for it.
    /// </summary>
    /// <param name="peer">The colleague a partner publishes.</param>
    bool IsPeerAvailableAndSecure(Records.PeerReference peer);
}