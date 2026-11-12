using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// The one owner of a conversation's channel, over the conversation's own database.
/// A process-wide copy keyed by conversation id spares every send a read of the database once the handshake is known here.
/// </summary>
public class ChannelMetadataStore : IChannelMetadataStore
{
    /// <summary>Holds each conversation's handshake, persisted at start and read back on a lookup that memory cannot answer.</summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// The message length a channel must afford for rich features to be believable, from <c>Morgana:AdaptiveMessaging:RichFeaturesMinLength</c>.
    /// A value of zero or less trusts every declaration.
    /// </summary>
    private readonly int richFeaturesMinLength;

    /// <summary>Logs each handshake as it is settled.</summary>
    private readonly ILogger logger;

    /// <summary>The handshakes this process has already registered or read back, keyed by conversation id.</summary>
    private readonly ConcurrentDictionary<string, ChannelMetadata> metadataByConversation = new();

    /// <summary>The last answer delivered to each conversation, as the user saw it, keyed by conversation id.</summary>
    private readonly ConcurrentDictionary<string, ChannelMessage> lastAnswerByConversation = new();

    /// <summary>Builds the store over the persistence service holding each conversation's handshake.</summary>
    public ChannelMetadataStore(
        IConversationPersistenceService conversationPersistenceService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.conversationPersistenceService = conversationPersistenceService;
        this.logger = logger;

        // Absent, the threshold is zero and every channel's declaration is trusted.
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

        // The record serves a process that restarts mid-conversation; the copy serves every send of this one.
        await conversationPersistenceService.SaveChannelMetadataAsync(conversationId, channelMetadata);
        metadataByConversation[conversationId] = channelMetadata;

        logger.LogInformation(
            "Channel metadata registered for {ConversationId}: channel={ChannelName}, delivery={DeliveryMode}, " +
            "rc={SupportsRichCards}, qr={SupportsQuickReplies}, str={SupportsStreaming}, md={SupportsMarkdown}, max={MaxMessageLength}",
            conversationId, channelMetadata.Coordinates.ChannelName, channelMetadata.Coordinates.DeliveryMode,
            channelMetadata.Capabilities.SupportsRichCards, channelMetadata.Capabilities.SupportsQuickReplies,
            channelMetadata.Capabilities.SupportsStreaming, channelMetadata.Capabilities.SupportsMarkdown,
            channelMetadata.Capabilities.MaxMessageLength);

        // The controller answers the channel with what Morgana will actually send it.
        return channelMetadata;
    }

    /// <inheritdoc/>
    public void EvictChannelMetadata(string conversationId)
    {
        // Both copies live and die with the conversation's actors, so nothing is kept for a conversation that ended.
        metadataByConversation.TryRemove(conversationId, out _);
        lastAnswerByConversation.TryRemove(conversationId, out _);
    }

    /// <inheritdoc/>
    public ValueTask RecordDeliveredMessageAsync(ChannelMessage deliveredMessage)
    {
        // Only what was said counts as an answer, which a later one replaces: warnings, errors and command frames are notices
        // about the conversation and leave the last answer as it was.
        if (string.Equals(deliveredMessage.MessageType, ChannelMessageTypes.Assistant, StringComparison.Ordinal)
             || string.Equals(deliveredMessage.MessageType, ChannelMessageTypes.Presentation, StringComparison.Ordinal))
        {
            lastAnswerByConversation[deliveredMessage.ConversationId] = deliveredMessage;
        }

        // The copy is in memory, so the write has completed by the time the caller receives the task.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<ChannelMessage?> GetLastAnswerAsync(string conversationId) =>
        // Absent until the channel delivers an answer in this process; the copy is in memory, so the result is ready at once.
        ValueTask.FromResult(lastAnswerByConversation.GetValueOrDefault(conversationId));

    /// <inheritdoc/>
    public async ValueTask<ChannelMetadata> GetChannelMetadataAsync(string conversationId)
    {
        // A handshake this process already knows is served from memory.
        if (metadataByConversation.TryGetValue(conversationId, out ChannelMetadata? knownChannelMetadata))
            return knownChannelMetadata;

        // A conversation this process never saw start: served on the handshake it persisted then.
        ChannelMetadata persistedChannelMetadata = await conversationPersistenceService.LoadChannelMetadataAsync(conversationId)
            ?? throw new InvalidOperationException(
                $"No channel metadata on record for conversation {conversationId}; " +
                "the start-conversation gate refuses to open a conversation without a handshake.");

        // Two sends racing on the first lookup keep the same copy.
        return metadataByConversation.GetOrAdd(conversationId, persistedChannelMetadata);
    }

    /// <summary>Turns what a channel declares into what Morgana will actually send it.</summary>
    private ChannelCapabilities NormaliseCapabilities(ChannelCapabilities declaredCapabilities)
    {
        // Each rule below can only take a capability away from the declaration.
        ChannelCapabilities effectiveCapabilities = declaredCapabilities;

        // Too short a message for rich features to be believable: a channel declaring no length cap
        // has nothing to be judged primitive by and keeps its declaration.
        if (declaredCapabilities.MaxMessageLength is { } maxMessageLength && richFeaturesMinLength > 0 && maxMessageLength < richFeaturesMinLength)
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

        // What every reader of the conversation's channel takes as is.
        return effectiveCapabilities;
    }
}