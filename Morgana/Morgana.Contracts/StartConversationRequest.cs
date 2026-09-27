namespace Morgana.Contracts;

/// <summary>
/// HTTP request model for starting a new conversation via REST API.
/// </summary>
/// <param name="ConversationId">Unique identifier for the conversation to create: a GUID in its 32-digit
/// format (<c>Guid.ToString("N")</c>), minted afresh by the channel. Any other shape is refused.</param>
/// <param name="ChannelMetadata">Metadata advertised by the originating channel/client at
/// the handshake: channel name (e.g. <c>cauldron</c>, <c>twilio-sms</c>, …) plus the expressive
/// capability budget. Required: Morgana rejects start requests from channels that do not
/// announce both their name and their capability budget.</param>
/// <param name="InitialContext">Optional initial context information</param>
public record StartConversationRequest(
    string ConversationId,
    ChannelMetadata? ChannelMetadata,
    Dictionary<string, object>? InitialContext = null);