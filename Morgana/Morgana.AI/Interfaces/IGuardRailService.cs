using Morgana.AI.Attributes;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for content moderation and policy enforcement on what reaches Morgana's models: the user's message, the results of the tools her agents call and the questions partners put to her agents.
/// It also examines what external sources offer (an MCP server's tools, a partner's card) before an agent may use them and answers whether a source is available and secure right now.
/// Decouples guard-rail logic from the actor infrastructure. GuardActor delegates entirely
/// to this service and is agnostic of the underlying implementation strategy.
/// Default implementation: LLMGuardRailService provides LLM-based policy evaluation.
/// Fail-safe contract: the user's message fails open, so a transient error never blocks legitimate traffic; a tool's result and a partner's question fail closed.
/// An offer that cannot be judged is refused and the refusal is not remembered, so the next admission asks again.
/// </summary>
public interface IGuardRailService
{
    /// <summary>
    /// Evaluates whether the given message complies with content and policy rules.
    /// The message is judged beside the last answer this process delivered to the conversation and alone when there is none.
    /// </summary>
    /// <param name="conversationId">
    /// Unique identifier of the ongoing conversation.
    /// Passed for correlation/logging purposes; implementations may use it to apply
    /// per-conversation policies or to enrich audit trails.
    /// </param>
    /// <param name="message">User message text to evaluate.</param>
    /// <param name="cancellationToken">Stops the judgment once whoever asked for it stops waiting.</param>
    /// <returns>
    /// A <see cref="Records.GuardRailResult"/> indicating whether the message is compliant
    /// and, when not, describing the violated rule.
    /// </returns>
    Task<Records.GuardRailResult> CheckUserMessageAsync(string conversationId, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Judges a tool result before the agent's model reads it.
    /// </summary>
    /// <param name="conversationId">Passed for correlation and logging.</param>
    /// <param name="toolName">The function the model called.</param>
    /// <param name="toolArguments">The arguments the model called the tool with, serialized as a JSON object; only the inspector reads them, as the call the result is judged against.</param>
    /// <param name="toolResult">The result serialized as JSON, as the tool loop hands it to the provider.</param>
    /// <param name="cancellationToken">Stops the judgment once whoever asked for it stops waiting.</param>
    /// <returns>
    /// Non-compliant when the result is to be withheld, with the finding in <c>Violation</c> for an operator's log.
    /// </returns>
    /// <remarks>Never throws: a result that cannot be judged is withheld.</remarks>
    Task<Records.GuardRailResult> CheckToolResultAsync(string conversationId, string toolName, string toolArguments, string toolResult, CancellationToken cancellationToken = default);

    /// <summary>
    /// Judges the question a partner puts to an agent of this installation before the agent reads it.
    /// </summary>
    /// <param name="conversationId">The conversation the question is served on, for correlation and charging.</param>
    /// <param name="partnerName">The partner asking, as the authentication gate proved it.</param>
    /// <param name="agentIntent">The agent asked.</param>
    /// <param name="agentTerritory">What the asked agent answers for, as its card publishes it.</param>
    /// <param name="question">The partner's question in its own words.</param>
    /// <param name="cancellationToken">Stops the judgment once whoever asked for it stops waiting.</param>
    /// <returns>
    /// Non-compliant when the question is to be declined, with the finding in <c>Violation</c> for an operator's log.
    /// </returns>
    /// <remarks>Never throws: a question that cannot be judged is declined, as every text from outside this installation is.</remarks>
    Task<Records.GuardRailResult> CheckPeerQuestionAsync(string conversationId, string partnerName, string agentIntent, string agentTerritory, string question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists an MCP server and admits its tools to the agents that declare it.
    /// </summary>
    /// <param name="serverAttribute">The server declaration carried by the agent class.</param>
    /// <returns>The server's tools when its offer is admitted, none otherwise; the verdict says which.</returns>
    /// <remarks>
    /// A listing identical to the one approved before is admitted without a model call and any other listing is examined once however many callers ask at the same time.
    /// A server that cannot be listed yields no tools and a FailClosed verdict that is not remembered; an earlier approval of it stands, since a session the server dropped says nothing about its offer.
    /// Never throws.
    /// </remarks>
    Task<Records.MCPServerAdmission> AdmitMCPServerToolsAsync(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Examines the description a partner's card gives its colleague before the colleague is offered to an agent.
    /// </summary>
    /// <param name="peer">The colleague the card describes.</param>
    /// <param name="functionName">The name under which the model would call the colleague.</param>
    /// <param name="cardDescription">The description the card carries; null when it carries none.</param>
    /// <returns>Non-compliant when the colleague is to be kept from the agent.</returns>
    /// <remarks>The offer is remembered like an MCP server's: an unchanged description is admitted without a model call and an unjudgeable one is not remembered. Never throws.</remarks>
    Task<Records.GuardRailResult> AdmitPartnerCardAsync(Records.PeerReference peer, string functionName, string? cardDescription);

    /// <summary>
    /// Whether Morgana holds a completed approval of the server's current offer.
    /// </summary>
    /// <param name="serverAttribute">The server declaration carried by the agent class.</param>
    /// <returns>False while the offer was never approved or a changed one is being examined and after a refusal.</returns>
    bool IsServerAvailableAndSecure(UsesMCPServerAttribute serverAttribute);

    /// <summary>
    /// Whether Morgana holds a completed approval of the partner's colleague.
    /// </summary>
    /// <param name="peer">The colleague a partner publishes.</param>
    /// <returns>False while the card was never approved or a changed one is being examined and after a refusal.</returns>
    /// <remarks>Asked only of a partner's colleague: a colleague of this installation is never examined, so nothing is held for it.</remarks>
    bool IsPeerAvailableAndSecure(Records.PeerReference peer);
}