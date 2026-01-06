using Akka.Actor;
using System.Text.Json.Serialization;

namespace Morgana;

/// <summary>
/// Central repository of immutable record types (DTOs) used throughout the Morgana conversation system.
/// Records are organized by functional area: conversation lifecycle, messaging, guards, presentation, and actor pipeline contexts.
/// </summary>
/// <remarks>
/// <para><strong>Design Philosophy:</strong></para>
/// <list type="bullet">
/// <item>Immutable records for thread-safety in actor message passing</item>
/// <item>Explicit types prevent message routing errors in actor system</item>
/// <item>Context wrappers preserve sender references across async operations (PipeTo pattern)</item>
/// </list>
/// </remarks>
public static class Records
{
    // ==========================================================================
    // CONVERSATION LIFECYCLE MESSAGES
    // ==========================================================================

    /// <summary>
    /// Confirmation message sent after a conversation is successfully created.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the created conversation</param>
    public record ConversationCreated(
        string ConversationId);

    /// <summary>
    /// Final response message sent from ConversationSupervisorActor to ConversationManagerActor after processing a user message.
    /// Contains the AI response, metadata, agent information, and optional quick reply buttons.
    /// </summary>
    /// <param name="Response">AI-generated response text</param>
    /// <param name="Classification">Intent classification result (e.g., "billing", "contract")</param>
    /// <param name="Metadata">Additional metadata from classification (confidence, error codes, etc.)</param>
    /// <param name="AgentName">Name of the agent that generated the response (e.g., "Morgana", "Morgana (Billing)")</param>
    /// <param name="AgentCompleted">Flag indicating if the agent completed its multi-turn interaction</param>
    /// <param name="QuickReplies">Optional list of quick reply buttons for guided user interactions</param>
    public record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null,
        bool AgentCompleted = false,
        List<AI.Records.QuickReply>? QuickReplies = null);

    /// <summary>
    /// Request to create a new conversation and initialize the actor hierarchy.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the new conversation</param>
    public record CreateConversation(
        string ConversationId);

    /// <summary>
    /// Request to terminate a conversation and stop all associated actors.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation to terminate</param>
    public record TerminateConversation(
        string ConversationId);

    // ==========================================================================
    // USER MESSAGE HANDLING
    // ==========================================================================

    /// <summary>
    /// User message submitted for processing through the conversation pipeline.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Text">User's message text</param>
    /// <param name="Timestamp">Timestamp when the message was created</param>
    public record UserMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp);

    // ==========================================================================
    // GUARD (CONTENT MODERATION) MESSAGES
    // ==========================================================================

    /// <summary>
    /// Request for content moderation check on a user message.
    /// Sent to GuardActor for two-level filtering (profanity + LLM policy check).
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Message">User message to check for policy violations</param>
    public record GuardCheckRequest(
        string ConversationId,
        string Message);

    /// <summary>
    /// Result of content moderation check from GuardActor.
    /// </summary>
    /// <param name="Compliant">True if message passes policy checks, false if violation detected</param>
    /// <param name="Violation">Description of policy violation if Compliant is false</param>
    public record GuardCheckResponse(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    // ==========================================================================
    // HTTP REQUEST/RESPONSE MODELS
    // ==========================================================================

    /// <summary>
    /// HTTP request model for starting a new conversation via REST API.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the conversation to create</param>
    /// <param name="InitialContext">Optional initial context information (reserved for future use)</param>
    public record StartConversationRequest(
        string ConversationId,
        string? InitialContext = null);

    /// <summary>
    /// HTTP request model for sending a message to a conversation via REST API.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the target conversation</param>
    /// <param name="Text">Message text from the user</param>
    /// <param name="Metadata">Optional metadata dictionary (reserved for future use)</param>
    public record SendMessageRequest(
        string ConversationId,
        string Text,
        Dictionary<string, object>? Metadata = null
    );

    // ==========================================================================
    // QUICK REPLY SYSTEM
    // ==========================================================================

    /// <summary>
    /// LLM-generated presentation response from ConversationSupervisorActor.
    /// Contains the welcome message and quick reply buttons for user interaction.
    /// Deserialized from JSON returned by the LLM when generating presentation messages.
    /// </summary>
    /// <param name="Message">Welcome/presentation message text (2-4 sentences)</param>
    /// <param name="QuickReplies">List of quick reply button definitions</param>
    public record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("quickReplies")] List<AI.Records.QuickReply> QuickReplies);

    /// <summary>
    /// Structured message sent to clients via SignalR with full metadata support.
    /// Supports different message types, quick replies, error codes, and agent information.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Text">Message text content</param>
    /// <param name="Timestamp">Timestamp when the message was created</param>
    /// <param name="MessageType">Type for client-side rendering ("assistant", "presentation", "system", "error")</param>
    /// <param name="QuickReplies">Optional list of interactive buttons for user</param>
    /// <param name="ErrorReason">Optional error code for error messages (e.g., "llm_error", "timeout")</param>
    /// <param name="AgentName">Optional name of the agent that generated the message</param>
    /// <param name="AgentCompleted">Flag indicating if the agent completed its task</param>
    public record StructuredMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp,
        string MessageType,
        List<AI.Records.QuickReply>? QuickReplies = null,
        string? ErrorReason = null,
        string? AgentName = null,
        bool AgentCompleted = false);

    // ==========================================================================
    // PRESENTATION FLOW MESSAGES
    // ==========================================================================

    /// <summary>
    /// Trigger message to generate and send the initial presentation/welcome message.
    /// Sent automatically when a conversation is created.
    /// </summary>
    public record GeneratePresentationMessage;

    /// <summary>
    /// Context containing the generated presentation message and available intents.
    /// Used internally by ConversationSupervisorActor to send presentation via SignalR.
    /// </summary>
    /// <param name="Message">Welcome message text (either LLM-generated or fallback)</param>
    /// <param name="Intents">List of available intent definitions</param>
    public record PresentationContext(
        string Message,
        List<AI.Records.IntentDefinition> Intents)
    {
        /// <summary>
        /// LLM-generated quick replies (takes precedence over Intents if available).
        /// If null, quick replies are derived from Intents directly.
        /// </summary>
        public List<AI.Records.QuickReply>? LlmQuickReplies { get; init; }
    }

    // ==========================================================================
    // CONTEXT WRAPPERS FOR BECOME/PIPETO PATTERN
    // ==========================================================================
    // These records wrap async operation results with the original sender reference
    // to ensure correct message routing after async operations complete.

    // --- ConversationSupervisorActor Contexts ---

    /// <summary>
    /// Processing context maintained throughout the conversation pipeline.
    /// Captures the original message, sender, and classification result as it flows through states.
    /// </summary>
    /// <param name="OriginalMessage">The user message being processed</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationManagerActor)</param>
    /// <param name="Classification">Intent classification result (populated after ClassifierActor processes message)</param>
    public record ProcessingContext(
        UserMessage OriginalMessage,
        IActorRef OriginalSender,
        AI.Records.ClassificationResult? Classification = null);

    /// <summary>
    /// Context wrapper for GuardActor response via PipeTo.
    /// Preserves the original processing context alongside the guard check result.
    /// </summary>
    /// <param name="Response">Guard check result (compliant or violation)</param>
    /// <param name="Context">Original processing context</param>
    public record GuardCheckContext(
        GuardCheckResponse Response,
        ProcessingContext Context);

    /// <summary>
    /// Context wrapper for ClassifierActor response via PipeTo.
    /// Preserves the original processing context alongside the classification result.
    /// </summary>
    /// <param name="Classification">Intent classification result</param>
    /// <param name="Context">Original processing context</param>
    public record ClassificationContext(
        AI.Records.ClassificationResult Classification,
        ProcessingContext Context);

    /// <summary>
    /// Context wrapper for RouterActor/Agent response via PipeTo.
    /// Preserves the original processing context alongside the agent's response.
    /// </summary>
    /// <param name="Response">Agent response (can be ActiveAgentResponse or AgentResponse)</param>
    /// <param name="Context">Original processing context</param>
    public record AgentContext(
        object Response,
        ProcessingContext Context);

    /// <summary>
    /// Context wrapper for active agent follow-up response via PipeTo.
    /// Used when routing subsequent messages directly to an active agent.
    /// </summary>
    /// <param name="Response">Agent's follow-up response</param>
    /// <param name="OriginalSender">Actor reference to reply to</param>
    public record FollowUpContext(
        AI.Records.AgentResponse Response,
        IActorRef OriginalSender);

    /// <summary>
    /// Context wrapper for ConversationSupervisorActor response via PipeTo.
    /// Wraps the final conversation response for delivery to ConversationManagerActor.
    /// </summary>
    /// <param name="Response">Final conversation response with AI message and metadata</param>
    public record SupervisorResponseContext(
        ConversationResponse Response);

    // --- RouterActor Contexts ---

    /// <summary>
    /// Context wrapper for specialized agent response via PipeTo in RouterActor.
    /// Captures agent reference, response, and original sender for proper routing.
    /// </summary>
    /// <param name="Response">Agent's response</param>
    /// <param name="AgentRef">Reference to the agent that generated the response</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record AgentResponseContext(
        AI.Records.AgentResponse Response,
        IActorRef AgentRef,
        IActorRef OriginalSender);

    // --- GuardActor Contexts ---

    /// <summary>
    /// Context wrapper for LLM policy check response via PipeTo in GuardActor.
    /// Preserves the original sender reference across the async LLM call.
    /// </summary>
    /// <param name="Response">LLM policy check result</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record LLMCheckContext(
        GuardCheckResponse Response,
        IActorRef OriginalSender);
}