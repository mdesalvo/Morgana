using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Morgana.AI;

/// <summary>
/// Morgana's activity source and meter with the glossary of every span, attribute, event and metric they publish.
/// One trace per turn: a <c>morgana.turn</c> span linked to its HTTP request, with the guard, classifier, router and agent spans beneath it.
/// Actors run on threads that break the ambient activity, so the turn's context travels inside the actor messages.
/// </summary>
public static class Telemetry
{
    // ==============================================================================
    // ACTIVITY SOURCE
    // ==============================================================================

    /// <summary>The one activity source every span of Morgana starts from.</summary>
    public static readonly ActivitySource Source = new ActivitySource("Morgana");

    /// <summary>
    /// The source and meter name of the model calls every tier makes, instrumented by <c>ConfigurationLanguageModelService</c>.
    /// One name serves every provider, which each span tells apart by its <c>gen_ai.system</c> attribute.
    /// </summary>
    public const string LLMChatClientSourceName = "Morgana.AI.LLM";

    // ==============================================================================
    // METER & METRICS
    // ==============================================================================

    /// <summary>The one meter every metric of Morgana is published on.</summary>
    public static readonly Meter MorganaMeter = new Meter("Morgana");

    /// <summary>Counts completed turns, tagged with <see cref="TurnIntent"/> and <see cref="TurnCompleted"/>.</summary>
    public static readonly Counter<long> TurnCounter =
        MorganaMeter.CreateCounter<long>("morgana.turn.count", description: "Number of completed conversation turns");

    /// <summary>Counts messages rejected by the guard.</summary>
    public static readonly Counter<long> GuardRejectionCounter =
        MorganaMeter.CreateCounter<long>("morgana.guard.rejections", description: "Number of messages rejected by content moderation");

    /// <summary>
    /// Counts security events at Morgana's doors, tagged by door, outcome, deciding layer and agent intent.
    /// A tool's name is never a tag, since an MCP server chooses its names and a hostile one would multiply the series without bound.
    /// </summary>
    public static readonly Counter<long> SecurityEvents =
        MorganaMeter.CreateCounter<long>("morgana.security.events", description: "Security events at Morgana's doors: messages rejected, tool results withheld and MCP tools refused");

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

    /// <summary>The dust charged per model call, tagged with <see cref="DustLlmRole"/> and <see cref="ConversationId"/>.</summary>
    public static readonly Counter<double> DustConsumed =
        MorganaMeter.CreateCounter<double>("morgana.dust.consumed", "dust", "Magic dust charged per LLM call");

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

    /// <summary>Activity wrapping one agent answering a colleague, nested under the asking agent's own activity.</summary>
    public const string ConsultationActivity = "morgana.consultation";

    // ==============================================================================
    // ATTRIBUTE NAMES — CONVERSATION
    // ==============================================================================

    /// <summary>The conversation's id.</summary>
    public const string ConversationId = "conversation.id";

    /// <summary>The role a charge is filed under: <c>Morgana</c> for the framework actors, <c>Morgana (Intent/Tier)</c> for an agent.</summary>
    public const string DustLlmRole = "dust.llm_role";

    // ==============================================================================
    // ATTRIBUTE NAMES — TURN
    // ==============================================================================

    /// <summary>The user's message, cut to 200 characters.</summary>
    public const string TurnUserMessage = "turn.user_message";

    /// <summary>The intent a counted turn was served by, absent when the turn closed before any intent served it; a tag on <see cref="TurnCounter"/>.</summary>
    public const string TurnIntent = "turnIntent";

    /// <summary>Whether a counted turn's agent handed the conversation back; a tag on <see cref="TurnCounter"/>.</summary>
    public const string TurnCompleted = "turnCompleted";

    // ==============================================================================
    // ATTRIBUTE NAMES — GUARD
    // ==============================================================================

    /// <summary>True if the message passed the guard check, false if blocked.</summary>
    public const string GuardCompliant = "guard.compliant";

    /// <summary>Description of the policy violation when guard.compliant is false.</summary>
    public const string GuardViolation = "guard.violation";

    /// <summary>The layer that decided the user's message.</summary>
    public const string GuardSource = "guard.source";

    // ==============================================================================
    // ATTRIBUTE NAMES — SECURITY
    // ==============================================================================

    /// <summary>Where the event happened; one of <see cref="SecurityDoors"/>.</summary>
    public const string SecurityDoor = "security.door";

    /// <summary>What happened there; one of <see cref="SecurityOutcomes"/>.</summary>
    public const string SecurityOutcome = "security.outcome";

    /// <summary>The layer that decided.</summary>
    public const string SecuritySource = "security.source";

    /// <summary>The guard's clinical finding, never the text it judged.</summary>
    public const string SecurityFinding = "security.finding";

    /// <summary>The technical cause of a check that could not judge, under the OpenTelemetry semantic-convention name.</summary>
    public const string ErrorType = "error.type";

    /// <summary>The function the model called.</summary>
    public const string ToolName = "tool.name";

    // ==============================================================================
    // ATTRIBUTE NAMES — CLASSIFIER
    // ==============================================================================

    /// <summary>Classified intent name returned by the LLM classifier.</summary>
    public const string ClassificationIntent = "classification.intent";

    /// <summary>Confidence score from 0.0 to 1.0 returned by the LLM classifier.</summary>
    public const string ClassificationConfidence = "classification.confidence";

    /// <summary>Classified metadata returned by the LLM classifier.</summary>
    public const string ClassificationMetadata = "classification.metadata";

    // ==============================================================================
    // ATTRIBUTE NAMES — ROUTER
    // ==============================================================================

    /// <summary>The intent the router selected the agent by.</summary>
    public const string RouterIntent = "router.intent";

    // ==============================================================================
    // ATTRIBUTE NAMES — AGENT
    // ==============================================================================

    /// <summary>The agent's class name, such as <c>BillingAgent</c>.</summary>
    public const string AgentName = "agent.name";

    /// <summary>Intent handled by this agent (e.g. "billing").</summary>
    public const string AgentIntent = "agent.intent";

    /// <summary>Identifier of this agent (e.g. "billing-conv12345").</summary>
    public const string AgentIdentifier = "agent.identifier";

    /// <summary>True if the agent completed its task (no follow-up expected).</summary>
    public const string AgentIsCompleted = "agent.is_completed";

    /// <summary>True if the agent returned quick replies to the user.</summary>
    public const string AgentHasQuickReplies = "agent.has_quick_replies";

    /// <summary>The names of the tools called during the turn, comma-separated in call order and never their arguments, since spans reach every exporter.</summary>
    public const string AgentToolsInvoked = "agent.tools_invoked";

    /// <summary>First 150 characters of the agent response, for non-IT readability.</summary>
    public const string AgentResponsePreview = "agent.response_preview";

    /// <summary>Time in milliseconds from agent start to first streaming chunk (TTFT).</summary>
    public const string AgentTtftMs = "agent.ttft_ms";

    // ==============================================================================
    // ATTRIBUTE NAMES — PEER CONSULTATION
    // ==============================================================================

    /// <summary>The intent of the agent that asked.</summary>
    public const string ConsultationCaller = "consultation.caller";

    /// <summary>Intent of the agent that answered.</summary>
    public const string ConsultationTarget = "consultation.target";

    /// <summary>Whether the answering agent declared the exchange unfinished — the signal that an exchange is failing to converge.</summary>
    public const string ConsultationAwaitingReply = "consultation.awaiting_reply";

    /// <summary>
    /// The question a colleague was asked, whole.
    /// It and the answer are the only record of an exchange the conversation keeps no trace of, so a preview would hide the sentence that went wrong.
    /// </summary>
    public const string ConsultationQuestion = "consultation.question";

    /// <summary>What the colleague answered, whole, for the same reason.</summary>
    public const string ConsultationAnswer = "consultation.answer";

    // ==============================================================================
    // EVENT NAMES
    // ==============================================================================

    /// <summary>Event emitted on the agent activity when the conversation is created.</summary>
    public const string CreateAgentConversation = "create_agent_conversation";

    /// <summary>Event emitted on the agent activity when the conversation is resumed.</summary>
    public const string ResumeAgentConversation = "resume_agent_conversation";

    /// <summary>Event emitted on the agent activity when the first streaming chunk arrives.</summary>
    public const string EventFirstChunk = "first_chunk";

    /// <summary>Event added to the span current when a tool's result is withheld.</summary>
    public const string EventToolWithheld = "morgana.security.tool_withheld";

    /// <summary>The values of <see cref="SecurityDoor"/>.</summary>
    public static class SecurityDoors
    {
        /// <summary>The user's message.</summary>
        public const string User = "user";

        /// <summary>A tool result before the model reads it.</summary>
        public const string Tool = "tool";

        /// <summary>An MCP tool joining an agent.</summary>
        public const string MCPAdmission = "mcp_admission";

        /// <summary>The provider refusing an agent's own request.</summary>
        public const string Agent = "agent";

        /// <summary>An MCP server's offer examined before agents may use it.</summary>
        public const string MCPOffer = "mcp_offer";

        /// <summary>The description of a partner's card examined before its colleague is offered.</summary>
        public const string PartnerCard = "partner_card";

        /// <summary>A partner's question before an agent of this installation reads it.</summary>
        public const string Peer = "peer";
    }

    /// <summary>The values of <see cref="SecurityOutcome"/>.</summary>
    public static class SecurityOutcomes
    {
        /// <summary>A request refused: the user's message or a partner's question by its guard, an agent's own request by the provider.</summary>
        public const string Rejected = "rejected";

        /// <summary>A tool result kept from the model.</summary>
        public const string Withheld = "withheld";

        /// <summary>A tool not run because its result was withheld earlier in the turn.</summary>
        public const string WithheldWithoutRunning = "withheld_without_running";

        /// <summary>An MCP tool, an MCP server's offer or a partner's card kept out of an agent.</summary>
        public const string Refused = "refused";

        /// <summary>A source came back with an offer other than the one examined before.</summary>
        public const string Changed = "changed";

        /// <summary>A call not sent because its source is not available and secure.</summary>
        public const string NotSent = "not_sent";
    }

    /// <summary>The values of <see cref="ErrorType"/> that are not an exception's name.</summary>
    internal static class ErrorTypes
    {
        /// <summary>The caller stopped waiting before the guard answered.</summary>
        public const string Timeout = "timeout";

        /// <summary>The guard model answered without a verdict in its shape.</summary>
        public const string UnreadableVerdict = "unreadable_verdict";
    }
}