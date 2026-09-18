using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// <see cref="IChannelMetadataStore"/> over the conversation's own database, with a process-wide
/// copy keyed by conversation id so a send does not reach SQLite once the handshake is known here.
/// </summary>
public class ChannelMetadataStore : IChannelMetadataStore
{
    /// <summary>
    /// Holds the handshake record of each conversation, persisted at start and read back on a lookup
    /// this process cannot answer from memory.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// Minimum message length a channel must afford for rich features to be believable, from
    /// <c>Morgana:AdaptiveMessaging:RichFeaturesMinLength</c>. Non-positive trusts every declaration.
    /// </summary>
    private readonly int richFeaturesMinLength;

    private readonly ILogger logger;

    /// <summary>
    /// Handshakes this process has already registered or read back, keyed by conversation id.
    /// </summary>
    private readonly ConcurrentDictionary<string, ChannelMetadata> metadataByConversation = new();

    /// <summary>Builds the store over the persistence service holding each conversation's handshake.</summary>
    public ChannelMetadataStore(
        IConversationPersistenceService conversationPersistenceService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.conversationPersistenceService = conversationPersistenceService;
        this.logger = logger;
        richFeaturesMinLength = configuration.GetValue("Morgana:AdaptiveMessaging:RichFeaturesMinLength", 0);
    }

    /// <inheritdoc/>
    public async Task<ChannelMetadata> RegisterChannelMetadataAsync(string conversationId, ChannelMetadata declaredChannelMetadata)
    {
        // ChannelName and DeliveryMode are trimmed and lowercased so their name spaces stay
        // case-insensitive end-to-end, whatever casing the channel announced itself with.
        ChannelMetadata channelMetadata = new ChannelMetadata
        {
            Coordinates = declaredChannelMetadata.Coordinates with
            {
                ChannelName = declaredChannelMetadata.Coordinates.ChannelName.Trim().ToLowerInvariant(),
                DeliveryMode = declaredChannelMetadata.Coordinates.DeliveryMode.Trim().ToLowerInvariant()
            },
            Capabilities = NormaliseCapabilities(declaredChannelMetadata.Capabilities)
        };

        await conversationPersistenceService.SaveChannelMetadataAsync(conversationId, channelMetadata);
        metadataByConversation[conversationId] = channelMetadata;

        logger.LogInformation(
            "Channel metadata registered for {ConversationId}: channel={ChannelName}, delivery={DeliveryMode}, " +
            "rc={SupportsRichCards}, qr={SupportsQuickReplies}, str={SupportsStreaming}, md={SupportsMarkdown}, max={MaxMessageLength}",
            conversationId, channelMetadata.Coordinates.ChannelName, channelMetadata.Coordinates.DeliveryMode,
            channelMetadata.Capabilities.SupportsRichCards, channelMetadata.Capabilities.SupportsQuickReplies,
            channelMetadata.Capabilities.SupportsStreaming, channelMetadata.Capabilities.SupportsMarkdown,
            channelMetadata.Capabilities.MaxMessageLength);

        return channelMetadata;
    }

    /// <inheritdoc/>
    public void EvictChannelMetadata(string conversationId) =>
        metadataByConversation.TryRemove(conversationId, out _);

    /// <inheritdoc/>
    public async ValueTask<ChannelMetadata> GetChannelMetadataAsync(string conversationId)
    {
        if (metadataByConversation.TryGetValue(conversationId, out ChannelMetadata? knownChannelMetadata))
            return knownChannelMetadata;

        // A conversation this process never saw start: served on the handshake it persisted then
        ChannelMetadata persistedChannelMetadata = await conversationPersistenceService.LoadChannelMetadataAsync(conversationId)
            ?? throw new InvalidOperationException(
                $"No channel metadata on record for conversation {conversationId}; " +
                "the start-conversation gate refuses to open a conversation without a handshake.");

        return metadataByConversation.GetOrAdd(conversationId, persistedChannelMetadata);
    }

    /// <summary>
    /// Turns what a channel declares into what Morgana will actually send it. A channel whose
    /// MaxMessageLength falls below the rich-features threshold is treated as primitive (no rich
    /// cards, no quick replies). A channel whose messages will need adapting gets no streaming either.
    /// </summary>
    private ChannelCapabilities NormaliseCapabilities(ChannelCapabilities declaredCapabilities)
    {
        ChannelCapabilities effectiveCapabilities = declaredCapabilities;

        // Too short a message for rich features to be believable: a channel declaring no length cap
        // has nothing to be judged primitive by and keeps its declaration.
        if (declaredCapabilities.MaxMessageLength is { } max && richFeaturesMinLength > 0 && max < richFeaturesMinLength)
            effectiveCapabilities = effectiveCapabilities with
            {
                SupportsRichCards = false,
                SupportsQuickReplies = false
            };

        // Chunks bypass the adapter, so a channel whose final message gets rewritten would first show
        // the raw text and then watch it change under the user's eyes. It receives the final message alone.
        bool willNeedAdaptation = !effectiveCapabilities.SupportsRichCards
                                  || !effectiveCapabilities.SupportsQuickReplies
                                  || !effectiveCapabilities.SupportsMarkdown;
        if (willNeedAdaptation && effectiveCapabilities.SupportsStreaming)
            effectiveCapabilities = effectiveCapabilities with { SupportsStreaming = false };

        return effectiveCapabilities;
    }
}
