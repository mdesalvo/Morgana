using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Morgana.AI.Telemetry;

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
/// Activity: morgana.turn             ← one per user message (root span, lives in supervisor for full pipeline duration)
///   link → HTTP span                 ← ActivityLink to the originating ASP.NET Core request span
///   ├── Activity: morgana.guard      ← content moderation (spans full guard-check duration)
///   ├── Activity: morgana.classifier ← intent classification (new requests only, spans full LLM call)
///   ├── Activity: morgana.router     ← agent selection marker (new requests only)
///   └── Activity: morgana.agent      ← agent execution (includes streaming)
///       event: "first_chunk"         ← TTFT marker
/// </code>
///
/// <para><strong>Context Propagation:</strong></para>
/// <para>Because Akka.NET actors run on different threads and break .NET's ambient
/// <c>Activity.Current</c>, the <see cref="ActivityContext"/> of the turn span is carried
/// explicitly inside <see cref="Records.ProcessingContext"/> and
/// <see cref="Records.AgentRequest"/>. Each actor reconstructs the
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
    // METER & METRICS
    // ==============================================================================

    /// <summary>
    /// The single <see cref="Meter"/> for all Morgana metrics (counters, histograms).
    /// </summary>
    public static readonly Meter MorganaMeter = new Meter("Morgana");

    /// <summary>Counts completed turns, tagged with intent and completion status.</summary>
    public static readonly Counter<long> TurnCounter =
        MorganaMeter.CreateCounter<long>("morgana.turn.count", description: "Number of completed conversation turns");

    /// <summary>Counts messages rejected by the guard.</summary>
    public static readonly Counter<long> GuardRejectionCounter =
        MorganaMeter.CreateCounter<long>("morgana.guard.rejections", description: "Number of messages rejected by content moderation");

    /// <summary>End-to-end turn duration in milliseconds (from UserMessage to response).</summary>
    public static readonly Histogram<double> TurnDuration =
        MorganaMeter.CreateHistogram<double>("morgana.turn.duration", "ms", "End-to-end turn duration");

    /// <summary>Guard check duration in milliseconds.</summary>
    public static readonly Histogram<double> GuardDuration =
        MorganaMeter.CreateHistogram<double>("morgana.guard.duration", "ms", "Guard check duration");

    /// <summary>Classifier LLM call duration in milliseconds.</summary>
    public static readonly Histogram<double> ClassifierDuration =
        MorganaMeter.CreateHistogram<double>("morgana.classifier.duration", "ms", "Intent classification duration");

    /// <summary>Agent time-to-first-token in milliseconds.</summary>
    public static readonly Histogram<double> AgentTtftHistogram =
        MorganaMeter.CreateHistogram<double>("morgana.agent.ttft", "ms", "Agent time-to-first-token");

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