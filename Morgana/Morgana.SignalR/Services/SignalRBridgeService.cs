using Microsoft.AspNetCore.SignalR;
using Morgana.Framework.Interfaces;
using Morgana.SignalR.Hubs;
using Morgana.SignalR.Messages;
using static Morgana.Framework.Records;

namespace Morgana.SignalR.Services;

/// <summary>
/// Implementation of ISignalRBridgeService that bridges the actor system with SignalR for real-time client communication.
/// Handles message formatting and delivery to SignalR conversation groups.
/// </summary>
/// <remarks>
/// <para><strong>Architecture Role:</strong></para>
/// <para>This service is injected into actors (particularly ConversationManagerActor) to enable them to send
/// messages to clients without direct coupling to SignalR infrastructure.</para>
/// <para><strong>Message Protocol:</strong></para>
/// <para>All messages sent via SignalR use the "ReceiveMessage" event with a structured JSON payload containing:
/// conversationId, text, timestamp, messageType, quickReplies, errorReason, agentName, agentCompleted</para>
/// <para><strong>Group-based Routing:</strong></para>
/// <para>Messages are sent to SignalR groups named by conversation ID. All clients in a group receive the message,
/// enabling multi-user scenarios (monitoring, collaboration, customer + agent viewing the same conversation).</para>
/// </remarks>
public class SignalRBridgeService : ISignalRBridgeService
{
    private readonly IHubContext<ConversationHub> hubContext;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the SignalRBridgeService.
    /// </summary>
    /// <param name="hubContext">SignalR hub context for sending messages to clients</param>
    /// <param name="logger">Logger instance for message delivery diagnostics</param>
    public SignalRBridgeService(IHubContext<ConversationHub> hubContext, ILogger logger)
    {
        this.hubContext = hubContext;
        this.logger = logger;
    }

    /// <summary>
    /// Sends a simple text message to a conversation group via SignalR.
    /// Delegates to SendStructuredMessageAsync with default parameters.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (SignalR group name)</param>
    /// <param name="text">Message text to send to clients</param>
    /// <param name="errorReason">Optional error reason code for error messages</param>
    /// <returns>Task representing the async send operation</returns>
    /// <remarks>
    /// This is a convenience method that wraps SendStructuredMessageAsync with:
    /// - messageType: "assistant"
    /// - agentName: "Morgana"
    /// - agentCompleted: false
    /// - quickReplies: null
    /// </remarks>
    public async Task SendMessageToConversationAsync(string conversationId, string text, string? errorReason = null)
    {
        await SendStructuredMessageAsync(conversationId, text, "assistant", null, errorReason, "Morgana", false);
    }

    /// <summary>
    /// Sends a structured message with full metadata to a conversation group via SignalR.
    /// Formats the message according to the client protocol and delivers to all clients in the conversation group.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (SignalR group name)</param>
    /// <param name="text">Message text to send to clients</param>
    /// <param name="messageType">Type of message for client-side rendering ("assistant", "presentation", "system", "error")</param>
    /// <param name="quickReplies">Optional list of quick reply buttons for user interaction</param>
    /// <param name="errorReason">Optional error reason code for error messages</param>
    /// <param name="agentName">Optional name of the agent that generated the response</param>
    /// <param name="agentCompleted">Flag indicating if the agent has completed its task</param>
    /// <param name="originalTimestamp">Timestamp of the message when create at UI level</param>
    /// <returns>Task representing the async send operation</returns>
    /// <remarks>
    /// <para><strong>Message Structure:</strong></para>
    /// <para>Creates a StructuredMessage and sends it to the SignalR group with this JSON format:</para>
    /// <code>
    /// {
    ///   "conversationId": "conv-123",
    ///   "text": "Message content here",
    ///   "timestamp": "2024-01-05T10:30:00Z",
    ///   "messageType": "assistant",
    ///   "quickReplies": [
    ///     { "id": "billing", "label": "📄 View Invoices", "value": "Show my invoices" }
    ///   ],
    ///   "errorReason": null,
    ///   "agentName": "Morgana (Billing)",
    ///   "agentCompleted": true
    /// }
    /// </code>
    /// <para><strong>Client-side Handler:</strong></para>
    /// <para>Clients listen for the "ReceiveMessage" event:</para>
    /// <code>
    /// connection.on("ReceiveMessage", (message) => {
    ///   console.log(`${message.agentName}: ${message.text}`);
    ///   if (message.agentCompleted) {
    ///     // Agent finished, return conversation to idle state
    ///   }
    ///   if (message.quickReplies) {
    ///     // Render quick reply buttons
    ///   }
    /// });
    /// </code>
    /// <para><strong>Logging:</strong></para>
    /// <para>Logs message type, conversation ID, agent name, and completion status for diagnostics.</para>
    /// </remarks>
    public async Task SendStructuredMessageAsync(
        string conversationId,
        string text,
        string messageType,
        List<QuickReply>? quickReplies = null,
        string? errorReason = null,
        string? agentName = null,
        bool agentCompleted = false,
        DateTime? originalTimestamp = null)
    {
        logger.LogInformation($"Sending {messageType} message to conversation {conversationId} via SignalR (agent: {agentName ?? "Morgana"}, completed: {agentCompleted})");

        // Create strongly-typed DTO with contract mapping
        SignalRMessage message = new SignalRMessage
        {
            ConversationId = conversationId,
            Text = text,
            Timestamp = originalTimestamp ?? DateTime.UtcNow,
            MessageType = messageType,
            QuickReplies = quickReplies,
            ErrorReason = errorReason,
            AgentName = agentName ?? "Morgana",
            AgentCompleted = agentCompleted
        };

        // Send strongly-typed DTO (SignalR serializes to JSON automatically)
        await hubContext.Clients.Group(conversationId)
            .SendAsync("ReceiveMessage", message);
    }
}