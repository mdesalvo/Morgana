using Microsoft.AspNetCore.SignalR;
using Morgana.AI.Interfaces;
using Morgana.Contracts;
using Morgana.Web.Hubs;

namespace Morgana.Web.Services;

/// <summary>
/// The transport of deliveryMode <c>signalr</c>: pushes a conversation's messages and stream chunks to its group on <see cref="MorganaHub"/>.
/// A push to a group nobody joined is lost without a trace, which is why a channel joins before it starts the conversation.
/// </summary>
public class SignalRChannelService : IChannelService
{
    /// <summary>Pushes to the group named after the conversation id, which every client of that conversation joined.</summary>
    private readonly IHubContext<MorganaHub> hubContext;

    /// <summary>Records every message pushed and every chunk that failed.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the transport to the hub it pushes through.</summary>
    /// <param name="hubContext">The hub's context, which reaches its groups from outside a hub call.</param>
    /// <param name="logger">Records the pushes.</param>
    public SignalRChannelService(IHubContext<MorganaHub> hubContext, ILogger logger)
    {
        this.hubContext = hubContext;
        this.logger = logger;
    }

    /// <summary>Pushes the message whole, as the <c>ReceiveMessage</c> event, to the conversation's group.</summary>
    /// <param name="channelMessage">The message, already fitted to the channel.</param>
    public async Task SendMessageAsync(ChannelMessage channelMessage)
    {
        logger.LogInformation(
            $"Sending structured message to conversation {channelMessage.ConversationId}: " +
            $"type={channelMessage.MessageType}, agent={channelMessage.AgentName}, completed={channelMessage.AgentCompleted}, " +
            $"#quickReplies={channelMessage.QuickReplies?.Count ?? 0}, hasRichCard={channelMessage.RichCard != null}");

        // No wait before the first message of a conversation: channels are required to join the group
        // before starting it, so there is always someone listening. See MorganaHub.JoinConversation.
        await hubContext.Clients.Group(channelMessage.ConversationId)
            .SendAsync("ReceiveMessage", channelMessage);
    }

    /// <summary>
    /// Pushes a piece of an answer as it streams, the bare text as the <c>ReceiveStreamChunk</c> event, to the conversation's group.
    /// A successful chunk is not logged, since a turn sends hundreds; the whole answer follows as a <c>ReceiveMessage</c>.
    /// </summary>
    /// <param name="conversationId">The conversation, which names the group.</param>
    /// <param name="chunkText">The text to append to the answer being drawn.</param>
    public async Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        try
        {
            // The text alone: the client appends it to the answer it is drawing, which the final message then replaces.
            await hubContext.Clients.Group(conversationId)
                .SendAsync("ReceiveStreamChunk", chunkText);
        }
        catch (Exception ex)
        {
            // A lost chunk costs only the progressive reveal, so the turn streams on and the final message still arrives.
            logger.LogError(ex, "Failed to send stream chunk to conversation {ConversationId}", conversationId);
        }
    }
}