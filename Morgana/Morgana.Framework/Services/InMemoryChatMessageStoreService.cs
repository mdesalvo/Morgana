using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Morgana.Framework.Services;

/// <summary>
/// ChatMessageStore implementation for persisting a single agent's conversation history.
/// </summary>
/// <remarks>
/// <para><strong>Lifecycle:</strong></para>
/// <code>
/// InvokingAsync → Called BEFORE agent invocation → Returns messages for LLM context
/// InvokedAsync → Called AFTER agent invocation → Stores new messages
/// </code>
/// <para><strong>Integration with Framework:</strong></para>
/// <code>
/// AgentThread.Serialize() → combines:
///   - InMemoryChatMessageStoreService.Serialize() (messages)
///   - MorganaContextProvider.Serialize() (context variables)
///   
/// AgentThread.Deserialize() → restores both independently via factories
/// </code>
/// </remarks>
public class InMemoryChatMessageStoreService : ChatMessageStore
{
    private readonly string conversationId;
    private readonly string intent;
    private readonly ILogger logger;
    
    // Private message storage for this agent instance
    private readonly List<ChatMessage> messages;

    /// <summary>
    /// Creates a message store from serialized state or fresh.
    /// Used by ChatMessageStoreFactory during thread creation/resumption.
    /// </summary>
    public InMemoryChatMessageStoreService(
        string conversationId,
        string intent,
        JsonElement? serializedState,
        JsonSerializerOptions? jsonSerializerOptions,
        ILogger logger)
    {
        this.conversationId = conversationId;
        this.intent = intent;
        this.logger = logger;
        this.messages = [];

        // Deserialize messages if resuming thread
        if (serializedState.HasValue && serializedState.Value.ValueKind == JsonValueKind.Object)
        {
            if (serializedState.Value.TryGetProperty("Messages", out JsonElement messagesElement))
            {
                List<ChatMessage>? deserializedMessages = 
                    messagesElement.Deserialize<List<ChatMessage>>(jsonSerializerOptions);
                
                if (deserializedMessages != null)
                {
                    messages.AddRange(deserializedMessages);
                    
                    logger.LogInformation(
                        $"{nameof(InMemoryChatMessageStoreService)} RESTORED {messages.Count} messages for agent '{intent}' in conversation '{conversationId}'");
                }
            }
        }
        else
        {
            logger.LogInformation(
                $"{nameof(InMemoryChatMessageStoreService)} CREATED new store for agent '{intent}' in conversation '{conversationId}'");
        }
    }

    /// <summary>
    /// Called BEFORE agent invocation to retrieve messages for LLM context.
    /// Returns all messages in ascending chronological order.
    /// </summary>
    public override ValueTask<IEnumerable<ChatMessage>> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            $"{nameof(InMemoryChatMessageStoreService)} INVOKING: Returning {messages.Count} messages for agent '{intent}'");

        // Return in ascending chronological order (required by framework)
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(messages.AsEnumerable());
    }

    /// <summary>
    /// Called AFTER agent invocation to store new messages.
    /// Receives all messages from the current turn (request + response).
    /// </summary>
    public override ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // Extract new messages from context
        IEnumerable<ChatMessage> newMessages = context.ChatMessageStoreMessages;
        
        messages.AddRange(newMessages);
        
        logger.LogInformation(
            $"{nameof(InMemoryChatMessageStoreService)} INVOKED: Added {newMessages.Count()} messages (total: {messages.Count}) for agent '{intent}'");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Serializes message store state for AgentThread persistence.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        logger.LogInformation(
            $"{nameof(InMemoryChatMessageStoreService)} SERIALIZING {messages.Count} messages for agent '{intent}'");

        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>()
            {
                { "ConversationId", JsonSerializer.SerializeToElement(conversationId, jsonSerializerOptions) },
                { "Intent", JsonSerializer.SerializeToElement(intent, jsonSerializerOptions) },
                { "Messages", JsonSerializer.SerializeToElement(messages, jsonSerializerOptions) }
            }, jsonSerializerOptions);
    }
}