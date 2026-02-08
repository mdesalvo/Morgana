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
    /// <param name="richCard">Optional rich card for presentation of structured data</param>
    /// <returns>Task representing the async send operation</returns>
    public async Task SendStructuredMessageAsync(
        string conversationId,
        string text,
        string messageType,
        List<QuickReply>? quickReplies = null,
        string? errorReason = null,
        string? agentName = null,
        bool agentCompleted = false,
        DateTime? originalTimestamp = null,
        RichCard? richCard = null)
    {
        logger.LogInformation(
            $"Sending structured message to conversation {conversationId}: " +
            $"type={messageType}, agent={agentName ?? "Morgana"}, completed={agentCompleted}, " +
            $"#quickReplies={quickReplies?.Count ?? 0}, hasRichCard={richCard != null}");

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
            AgentCompleted = agentCompleted,
            RichCard = richCard
        };

        // Send strongly-typed DTO (SignalR serializes to JSON automatically)
        await hubContext.Clients.Group(conversationId)
            .SendAsync("ReceiveMessage", message);
    }

    /// <summary>
    /// Sends a streaming chunk to a conversation group via SignalR for progressive response rendering.
    /// Optimized for high-frequency streaming with minimal overhead (no logging, fire-and-forget).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (SignalR group name)</param>
    /// <param name="chunkText">Partial response text to append to the current message</param>
    /// <returns>Task representing the async send operation</returns>
    /// <remarks>
    /// <para><strong>Streaming Protocol:</strong></para>
    /// <para>Sends chunks via "ReceiveStreamChunk" SignalR event with minimal payload:</para>
    /// <code>
    /// {
    ///   "text": "chunk content here"
    /// }
    /// </code>
    /// <para><strong>No Logging:</strong></para>
    /// <para>Intentionally omits logging to avoid spamming logs with hundreds of partial text fragments.
    /// Only errors are logged if chunk delivery fails.</para>
    /// <para><strong>Client Expectations:</strong></para>
    /// <para>Clients should buffer chunks and append them to the current message being displayed.
    /// The final complete message arrives via the standard "ReceiveMessage" event with full metadata.</para>
    /// </remarks>
    public async Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        try
        {
            // Send chunk with minimal payload (just the text)
            await hubContext.Clients.Group(conversationId)
                .SendAsync("ReceiveStreamChunk", chunkText);
        }
        catch (Exception ex)
        {
            // Log errors but don't propagate - continue streaming
            logger.LogError(ex, $"Failed to send stream chunk to conversation {conversationId}");
        }
    }
}