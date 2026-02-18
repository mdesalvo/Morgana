using System.Diagnostics;

namespace Morgana.Framework.Telemetry;

/// <summary>
/// Central OpenTelemetry instrumentation hub for the Morgana framework.
/// Provides the shared <see cref="ActivitySource"/> and all activity/attribute name constants
/// used across the actor pipeline to ensure naming consistency.
/// </summary>
/// <remarks>
/// <para><strong>Design Philosophy:</strong></para>
/// <para>All tracing in Morgana flows from a single <see cref="ActivitySource"/> with name "Morgana".
/// This produces one coherent trace per conversation that is readable both by IT (latencies, errors,
/// classification confidence) and by non-IT audiences (intent, agent name, response preview).</para>
///
/// <para><strong>Trace Structure:</strong></para>
/// <code>
/// Trace: morgana.conversation  [conversationId]
/// │
/// ├── Activity: morgana.turn              ← one per user message
/// │   ├── Activity: morgana.guard         ← content moderation
/// │   ├── Activity: morgana.classifier    ← intent classification (new requests only)
/// │   ├── Activity: morgana.router        ← agent selection (new requests only)
/// │   └── Activity: morgana.agent         ← agent execution
/// │       event: "first_chunk"            ← TTFT marker
/// │
/// └── Activity: morgana.conversation.end ← on TerminateConversation
/// </code>
///
/// <para><strong>Context Propagation:</strong></para>
/// <para>Because Akka.NET actors run on different threads and break .NET's ambient
/// <c>Activity.Current</c>, the <see cref="ActivityContext"/> of the turn span is carried
/// explicitly inside <see cref="Morgana.Framework.Records.ProcessingContext"/> and
/// <see cref="Morgana.Framework.Records.AgentRequest"/>. Each actor reconstructs the
/// parent link via <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>.</para>
/// </remarks>
public static class MorganaTelemetry
{
    // ==============================================================================
    // ACTIVITY SOURCE
    // ==============================================================================

    /// <summary>
    /// The single <see cref="ActivitySource"/> for the entire Morgana framework.
    /// </summary>
    public static readonly ActivitySource Source = new ActivitySource("Morgana");

    // ==============================================================================
    // ACTIVITY NAMES
    // ==============================================================================

    /// <summary>Activity wrapping a single user turn (from HTTP POST to final response).</summary>
    public const string TurnActivity = "morgana.turn";

    /// <summary>Activity wrapping the GuardActor content moderation check.</summary>
    public const string GuardActivity = "morgana.guard";

    /// <summary>Activity wrapping the ClassifierActor intent classification.</summary>
    public const string ClassifierActivity = "morgana.classifier";

    /// <summary>Activity wrapping the RouterActor agent selection.</summary>
    public const string RouterActivity = "morgana.router";

    /// <summary>Activity wrapping a MorganaAgent execution (includes streaming).</summary>
    public const string AgentActivity = "morgana.agent";

    // ==============================================================================
    // ATTRIBUTE NAMES — CONVERSATION
    // ==============================================================================

    /// <summary>Unique identifier of the conversation. Maps to conversationId.</summary>
    public const string ConversationId = "conversation.id";

    // ==============================================================================
    // ATTRIBUTE NAMES — TURN
    // ==============================================================================

    /// <summary>User message text, truncated to 200 characters.</summary>
    public const string TurnUserMessage = "turn.user_message";

    // ==============================================================================
    // ATTRIBUTE NAMES — GUARD
    // ==============================================================================

    /// <summary>True if the message passed the guard check, false if blocked.</summary>
    public const string GuardCompliant = "guard.compliant";

    /// <summary>Description of the policy violation when guard.compliant is false.</summary>
    public const string GuardViolation = "guard.violation";

    // ==============================================================================
    // ATTRIBUTE NAMES — CLASSIFIER
    // ==============================================================================

    /// <summary>Classified intent name returned by the LLM classifier.</summary>
    public const string ClassificationIntent = "classification.intent";

    /// <summary>Confidence score from 0.0 to 1.0 returned by the LLM classifier.</summary>
    public const string ClassificationConfidence = "classification.confidence";

    // ==============================================================================
    // ATTRIBUTE NAMES — ROUTER
    // ==============================================================================

    /// <summary>Intent used to select the agent (matches classification.intent on success).</summary>
    public const string RouterIntent = "router.intent";

    /// <summary>Akka.NET path of the agent actor selected by the router.</summary>
    public const string RouterAgentPath = "router.agent_path";

    // ==============================================================================
    // ATTRIBUTE NAMES — AGENT
    // ==============================================================================

    /// <summary>Display name of the agent (e.g. "BillingAgent").</summary>
    public const string AgentName = "agent.name";

    /// <summary>Intent handled by this agent (e.g. "billing").</summary>
    public const string AgentIntent = "agent.intent";

    /// <summary>Identifier of this agent (e.g. "billing-conv12345").</summary>
    public const string AgentIdentifier = "agent.identifier";

    /// <summary>True if the agent completed its task (no follow-up expected).</summary>
    public const string AgentIsCompleted = "agent.is_completed";

    /// <summary>True if the agent returned quick replies to the user.</summary>
    public const string AgentHasQuickReplies = "agent.has_quick_replies";

    /// <summary>First 150 characters of the agent response, for non-IT readability.</summary>
    public const string AgentResponsePreview = "agent.response_preview";

    /// <summary>Time in milliseconds from agent start to first streaming chunk (TTFT).</summary>
    public const string AgentTtftMs = "agent.ttft_ms";

    // ==============================================================================
    // EVENT NAMES
    // ==============================================================================

    /// <summary>Event emitted on the agent activity when the conversation is created.</summary>
    public const string CreateAgentConversation = "create_agent_conversation";

    /// <summary>Event emitted on the agent activity when the conversation is resumed.</summary>
    public const string ResumeAgentConversation = "resume_agent_conversation";

    /// <summary>Event emitted on the agent activity when the first streaming chunk arrives.</summary>
    public const string EventFirstChunk = "first_chunk";
}