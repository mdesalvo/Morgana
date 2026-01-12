namespace Morgana.Framework.Interfaces;

/// <summary>
/// Service interface for bridging the actor system with SignalR for real-time client communication.
/// Abstracts SignalR hub context interactions and provides structured message delivery to conversation groups.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service acts as a bridge between the Akka.NET actor system and SignalR, allowing actors to send
/// messages to clients without direct knowledge of SignalR implementation details.</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item>Format messages according to client protocol (messageType, metadata, quick replies)</item>
/// <item>Route messages to appropriate SignalR conversation groups</item>
/// <item>Handle error notifications and delivery failures</item>
/// <item>Support rich message types (text, presentation, quick replies, agent metadata)</item>
/// </list>
/// <para><strong>Usage Pattern:</strong></para>
/// <para>Injected into actors (particularly ConversationManagerActor and ConversationSupervisorActor)
/// to send responses back to clients after processing through the actor pipeline.</para>
/// </remarks>
public interface ISignalRBridgeService
{
    /// <summary>
    /// Sends a simple text message to a conversation group via SignalR.
    /// Basic message delivery without structured metadata or quick replies.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (SignalR group name)</param>
    /// <param name="text">Message text to send to clients</param>
    /// <param name="errorReason">Optional error reason code for error messages (e.g., "guard_violation", "timeout")</param>
    /// <returns>Task representing the async send operation</returns>
    /// <remarks>
    /// <para><strong>Use cases:</strong></para>
    /// <list type="bullet">
    /// <item>Simple text responses from agents</item>
    /// <item>Error notifications to clients</item>
    /// <item>System messages (e.g., "Processing...", "Connection restored")</item>
    /// </list>
    /// <para><strong>Note:</strong> For rich messages with quick replies or agent metadata, use SendStructuredMessageAsync instead.</para>
    /// </remarks>
    Task SendMessageToConversationAsync(
        string conversationId,
        string text,
        string? errorReason = null);

    /// <summary>
    /// Sends a structured message with full metadata to a conversation group via SignalR.
    /// Supports message types, quick replies, agent identification, and completion status.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (SignalR group name)</param>
    /// <param name="text">Message text to send to clients</param>
    /// <param name="messageType">Type of message for client-side rendering ("assistant", "presentation", "system", "error")</param>
    /// <param name="quickReplies">Optional list of quick reply buttons for user interaction</param>
    /// <param name="errorReason">Optional error reason code for error messages (e.g., "llm_error", "supervisor_error")</param>
    /// <param name="agentName">Optional name of the agent that generated the response (e.g., "Morgana", "Morgana (Billing)")</param>
    /// <param name="agentCompleted">Flag indicating if the agent has completed its task (affects UI state, conversation flow)</param>
    /// <returns>Task representing the async send operation</returns>
    /// <remarks>
    /// <para><strong>Message Types:</strong></para>
    /// <list type="bullet">
    /// <item><term>"assistant"</term><description>Standard AI response from an agent</description></item>
    /// <item><term>"presentation"</term><description>Initial welcome message with quick replies</description></item>
    /// <item><term>"system"</term><description>System notifications (not AI-generated)</description></item>
    /// <item><term>"error"</term><description>Error messages with errorReason code</description></item>
    /// </list>
    /// <para><strong>Quick Replies:</strong></para>
    /// <para>Interactive buttons displayed to the user for common actions or intent selection.
    /// Used primarily in presentation messages to guide users to available capabilities.</para>
    /// <para><strong>Agent Metadata:</strong></para>
    /// <list type="bullet">
    /// <item><term>agentName</term><description>Displayed in UI to show which specialized agent is responding</description></item>
    /// <item><term>agentCompleted</term><description>When true, signals the client that the multi-turn interaction is complete
    /// and the conversation returns to idle state (affects UI indicators, enables new intent classification)</description></item>
    /// </list>
    /// <para><strong>Example Usage:</strong></para>
    /// <code>
    /// // Send presentation with quick replies
    /// await SendStructuredMessageAsync(
    ///     conversationId: "conv-123",
    ///     text: "Welcome! How can I help you?",
    ///     messageType: "presentation",
    ///     quickReplies: [new QuickReply("billing", "📄 View Invoices", "Show my invoices")],
    ///     agentName: "Morgana",
    ///     agentCompleted: false
    /// );
    ///
    /// // Send agent response with completion
    /// await SendStructuredMessageAsync(
    ///     conversationId: "conv-123",
    ///     text: "Here are your recent invoices...",
    ///     messageType: "assistant",
    ///     quickReplies: null,
    ///     agentName: "Morgana (Billing)",
    ///     agentCompleted: true  // Billing agent finished, return to idle
    /// );
    /// </code>
    /// </remarks>
    Task SendStructuredMessageAsync(
        string conversationId,
        string text,
        string messageType,
        List<Records.QuickReply>? quickReplies = null,
        string? errorReason = null,
        string? agentName = null,
        bool agentCompleted = false);
}