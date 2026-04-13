using Microsoft.AspNetCore.SignalR;
using Morgana.AI.Interfaces;
using Morgana.Web.Hubs;
using static Morgana.AI.Records;

namespace Morgana.Web.Services;

/// <summary>
/// SignalR-backed implementation of <see cref="IChannelService"/>. This is the reference
/// channel powering the Cauldron web UI and advertises the full expressive capability
/// set (rich cards, quick replies, streaming, markdown).
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
public class SignalRChannelService : IChannelService
{
    /// <summary>
    /// SignalR hub context used to push messages to connected clients. Messages are routed
    /// to groups named after the conversation id, so every client that joined that group
    /// (via <c>MorganaHub.JoinConversation</c>) receives the payload through the
    /// "ReceiveMessage" / "ReceiveStreamChunk" events.
    /// </summary>
    private readonly IHubContext<MorganaHub> hubContext;

    /// <summary>
    /// Logger for diagnostic output. Emits informational entries on successful sends and
    /// error entries when the underlying SignalR dispatch fails.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Channel metadata advertised by the SignalR + Cauldron channel: identifies itself as
    /// <c>"cauldron"</c> and reuses the shared <see cref="ChannelCapabilities.Default"/>
    /// singleton for the full feature set.
    /// </summary>
    public ChannelMetadata Metadata { get; } = new ChannelMetadata("cauldron", ChannelCapabilities.Default);

    /// <summary>
    /// Initializes a new instance of the SignalRChannelService.
    /// </summary>
    /// <param name="hubContext">SignalR hub context for sending messages to clients</param>
    /// <param name="logger">Logger instance for message delivery diagnostics</param>
    public SignalRChannelService(IHubContext<MorganaHub> hubContext, ILogger logger)
    {
        this.hubContext = hubContext;
        this.logger = logger;
    }

    /// <summary>
    /// Publishes a <see cref="ChannelMessage"/> to the SignalR group matching
    /// <see cref="ChannelMessage.ConversationId"/> via the "ReceiveMessage" event.
    /// The DTO is forwarded as-is and serialized by SignalR's JSON pipeline; the wire
    /// format stays compatible with Cauldron's client-side <c>SignalRMessage</c> schema.
    /// </summary>
    /// <param name="message">The fully-formed channel message to deliver.</param>
    /// <returns>Task representing the async send operation</returns>
    public async Task SendMessageAsync(ChannelMessage message)
    {
        logger.LogInformation(
            $"Sending structured message to conversation {message.ConversationId}: " +
            $"type={message.MessageType}, agent={message.AgentName}, completed={message.AgentCompleted}, " +
            $"#quickReplies={message.QuickReplies?.Count ?? 0}, hasRichCard={message.RichCard != null}");

        await hubContext.Clients.Group(message.ConversationId)
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
            logger.LogError(ex, "Failed to send stream chunk to conversation {ConversationId}", conversationId);
        }
    }
}
