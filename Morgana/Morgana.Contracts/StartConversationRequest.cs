namespace Morgana.Contracts;

/// <summary>HTTP request model for starting a new conversation via REST API.</summary>
/// <param name="ConversationId">A GUID in its 32-digit format (<c>Guid.ToString("N")</c>) minted afresh by the channel.</param>
/// <param name="ChannelMetadata">The channel's name and capability budget, required: Morgana rejects a start request that does not announce both.</param>
// Any ConversationId of another shape is refused.
public record StartConversationRequest(
    string ConversationId,
    ChannelMetadata? ChannelMetadata);
