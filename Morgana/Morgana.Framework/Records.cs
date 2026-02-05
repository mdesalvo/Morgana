using Akka.Actor;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Morgana.Framework;

/// <summary>
/// Central repository of immutable record types (DTOs) used throughout the Morgana framework.
/// Records are organized by functional area: agent communication, classification, prompts, tools and presentation.
/// </summary>
/// <remarks>
/// <para><strong>Design Philosophy:</strong></para>
/// <list type="bullet">
/// <item>Immutable records for thread-safety in actor message passing</item>
/// <item>Explicit types prevent message routing errors in actor system</item>
/// <item>JSON serialization support for LLM interactions and configuration loading</item>
/// <item>Context wrappers preserve sender references across async operations (PipeTo pattern)</item>
/// </list>
/// </remarks>
public static class Records
{
    // ==========================================================================
    // CONVERSATION LIFECYCLE MESSAGES
    // ==========================================================================

    /// <summary>
    /// Final response message sent from ConversationSupervisorActor to ConversationManagerActor after processing a user message.
    /// Contains the AI response, metadata, agent information, and optional quick reply buttons.
    /// </summary>
    /// <param name="Response">AI-generated response text</param>
    /// <param name="Classification">Intent classification result (e.g., "billing", "contract")</param>
    /// <param name="Metadata">Additional metadata from classification (confidence, error codes, etc.)</param>
    /// <param name="AgentName">Name of the agent that generated the response (e.g., "Morgana", "Morgana (Billing)")</param>
    /// <param name="AgentCompleted">Flag indicating if the agent completed its multi-turn interaction</param>
    /// <param name="QuickReplies">Optional list of quick reply buttons for guided user interactions</param>
    /// <param name="OriginalTimestamp">Optional timestamp of the message when created at UI level</param>
    public record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null,
        bool AgentCompleted = false,
        List<QuickReply>? QuickReplies = null,
        DateTime? OriginalTimestamp = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Request to create a new conversation and initialize the actor hierarchy.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the new conversation</param>
    /// <param name="IsRestore">Flag indicating that the conversation is being created or restored</param>
    public record CreateConversation(
        string ConversationId,
        bool IsRestore);

    /// <summary>
    /// Request to terminate a conversation and stop all associated actors.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation to terminate</param>
    public record TerminateConversation(
        string ConversationId);

    // ==========================================================================
    // CONVERSATION PERSISTENCE
    // ==========================================================================

    /// <summary>
    /// Configuration options for conversation persistence.
    /// </summary>
    /// <remarks>
    /// <para><strong>Configuration Example:</strong></para>
    /// <code>
    /// {
    ///   "Morgana": {
    ///     "ConversationPersistence": {
    ///       "StoragePath": "C:/MorganaData",
    ///       "EncryptionKey": "your-base64-encoded-256-bit-key"
    ///     }
    ///   }
    /// }
    /// </code>
    /// <para><strong>Generating an Encryption Key:</strong></para>
    /// <code>
    /// // C# code to generate a secure 256-bit key
    /// using System.Security.Cryptography;
    /// byte[] key = new byte[32];
    /// RandomNumberGenerator.Fill(key);
    /// string base64Key = Convert.ToBase64String(key);
    /// Console.WriteLine(base64Key);
    /// </code>
    /// </remarks>
    public record ConversationPersistenceOptions
    {
        /// <summary>
        /// Directory path where conversation files will be stored.
        /// Directory will be created if it doesn't exist.
        /// </summary>
        /// <example>C:/MorganaData</example>
        public string StoragePath { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded 256-bit AES encryption key for conversation data.<br/>
        /// CRITICAL: Keep this key secure and never commit it to source control.
        /// </summary>
        /// <example>3q2+7w8e9r0t1y2u3i4o5p6a7s8d9f0g1h2j3k4l5z6x7c8v9b0n1m2==</example>
        public string EncryptionKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request to restore active agent state when resuming a conversation from persistence.
    /// Sent to ConversationSupervisor after conversation resume to set activeAgent and activeAgentIntent.
    /// </summary>
    /// <param name="AgentIntent">Intent of the agent that was last active (e.g., "billing", "contract")</param>
    public record RestoreActiveAgent(string AgentIntent);

    /// <summary>
    /// Request sent to RouterActor to restore/resolve an agent by intent.
    /// Returns the agent reference if successful, or null if intent is invalid.
    /// Router will cache the agent for future routing operations.
    /// </summary>
    /// <param name="AgentIntent">Intent name to resolve agent for</param>
    public record RestoreAgentRequest(string AgentIntent);

    /// <summary>
    /// Response from RouterActor containing the resolved agent reference.
    /// Null AgentRef indicates the intent could not be resolved to a valid agent.
    /// </summary>
    /// <param name="AgentIntent">Original intent requested</param>
    /// <param name="AgentRef">Resolved agent reference, or null if not found</param>
    public record RestoreAgentResponse(string AgentIntent, IActorRef? AgentRef);

    /// <summary>
    /// Chat message record for UI consumption (Cauldron).
    /// Represents a single message in the conversation history, mapped from Microsoft.Agents.AI.ChatMessage.
    /// </summary>
    /// <remarks>
    /// This record is optimized for Blazor UI rendering and includes UI-specific fields like AgentName, QuickReplies, etc.
    /// </remarks>
    public record MorganaChatMessage
    {
        /// <summary>
        /// Unique identifier of the conversation this message belongs to.
        /// </summary>
        public required string ConversationId { get; init; }

        /// <summary>
        /// Message text content displayed to the user.
        /// Extracted from TextContent blocks in ChatMessage.Content.
        /// </summary>
        public required string Text { get; init; }

        /// <summary>
        /// Timestamp when the message was created or received.
        /// Mapped from ChatMessage.CreatedAt.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Type of message determining styling and behavior.
        /// Derived from ChatMessage.Role (user/assistant).
        /// </summary>
        public required MessageType Type { get; init; }

        /// <summary>
        /// Gets the message role for CSS styling ("user" or "assistant").
        /// </summary>
        public string Role => Type switch
        {
            MessageType.User => "user",
            _ => "assistant"
        };

        /// <summary>
        /// Name of the agent that generated this message.
        /// Examples: "User", "Morgana (billing)", "Morgana (contract)", ...
        /// </summary>
        public required string AgentName { get; init; }

        /// <summary>
        /// Indicates whether the agent has completed its task.
        /// Mapped from SQLite is_active column: true when is_active = 0, false when is_active = 1.
        /// </summary>
        public required bool AgentCompleted { get; init; }

        /// <summary>
        /// Optional list of quick reply buttons attached to this message.
        /// Reconstructed from SetQuickReplies tool calls when loading conversation history.
        /// </summary>
        public List<QuickReply>? QuickReplies { get; init; }

        /// <summary>
        /// Optional flag indicating that this is the last message of a resumed conversation.
        /// </summary>
        public bool? IsLastHistoryMessage { get; init; }
    }

    /// <summary>
    /// Enumeration of message types for styling and behavior differentiation.
    /// </summary>
    public enum MessageType
    {
        /// <summary>
        /// Message from the user.
        /// Displayed on the right side with user styling.
        /// </summary>
        User,

        /// <summary>
        /// Regular response from an agent.
        /// Displayed on the left side with agent avatar and assistant styling.
        /// </summary>
        Assistant
    }

    // ==========================================================================
    // RATE LIMITING
    // ==========================================================================

    /// <summary>
    /// Configuration options for conversation rate limiting.
    /// </summary>
    /// <remarks>
    /// <para><strong>Configuration Example:</strong></para>
    /// <code>
    /// {
    ///   "Morgana": {
    ///     "RateLimiting": {
    ///       "Enabled": true,
    ///       "MaxMessagesPerMinute": 5,
    ///       "MaxMessagesPerHour": 30,
    ///       "MaxMessagesPerDay": 100,
    ///       "ErrorMessagePerMinute": "✋ Whoa there! You're casting spells too quickly...",
    ///       "ErrorMessagePerHour": "⏰ You've reached your hourly spell quota...",
    ///       "ErrorMessagePerDay": "🌙 You've exhausted today's magical energy...",
    ///       "ErrorMessageDefault": "⚠️ You're sending messages too quickly..."
    ///     }
    ///   }
    /// }
    /// </code>
    /// </remarks>
    public record RateLimitOptions
    {
        /// <summary>
        /// Master toggle for rate limiting feature.
        /// Set to false to disable all rate limiting (useful for development/testing).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum messages allowed per minute per conversation.
        /// Prevents burst spam. Set to 0 to disable this check.
        /// </summary>
        /// <example>5</example>
        public int MaxMessagesPerMinute { get; set; } = 5;

        /// <summary>
        /// Maximum messages allowed per hour per conversation.
        /// Prevents sustained abuse. Set to 0 to disable this check.
        /// </summary>
        /// <example>30</example>
        public int MaxMessagesPerHour { get; set; } = 30;

        /// <summary>
        /// Maximum messages allowed per day per conversation.
        /// Enforces daily quotas. Set to 0 to disable this check.
        /// </summary>
        /// <example>100</example>
        public int MaxMessagesPerDay { get; set; } = 100;

        /// <summary>
        /// Error message displayed when per-minute limit is exceeded.
        /// Supports placeholders: {limit} for the actual limit value.
        /// </summary>
        /// <example>✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again.</example>
        public string ErrorMessagePerMinute { get; set; } = 
            "✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again.";

        /// <summary>
        /// Error message displayed when per-hour limit is exceeded.
        /// Supports placeholders: {limit} for the actual limit value.
        /// </summary>
        /// <example>⏰ You've reached your hourly spell quota. The magic cauldron needs time to recharge!</example>
        public string ErrorMessagePerHour { get; set; } = 
            "⏰ You've reached your hourly spell quota. The magic cauldron needs time to recharge!";

        /// <summary>
        /// Error message displayed when per-day limit is exceeded.
        /// Supports placeholders: {limit} for the actual limit value.
        /// </summary>
        /// <example>🌙 You've exhausted today's magical energy. Return tomorrow for more spells!</example>
        public string ErrorMessagePerDay { get; set; } = 
            "🌙 You've exhausted today's magical energy. Return tomorrow for more spells!";

        /// <summary>
        /// Default error message for unknown/generic rate limit violations.
        /// </summary>
        /// <example>⚠️ You're sending messages too quickly. Please slow down.</example>
        public string ErrorMessageDefault { get; set; } = 
            "⚠️ You're sending messages too quickly. Please slow down.";
    }

    /// <summary>
    /// Result of a rate limit check operation.
    /// </summary>
    /// <param name="IsAllowed">Whether the request is allowed to proceed</param>
    /// <param name="ViolatedLimit">Description of which limit was exceeded (null if allowed)</param>
    /// <param name="RetryAfterSeconds">Suggested wait time in seconds before retrying (null if allowed)</param>
    public record RateLimitResult(
        bool IsAllowed,
        string? ViolatedLimit = null,
        int? RetryAfterSeconds = null);

    // ==========================================================================
    // USER MESSAGE HANDLING
    // ==========================================================================

    /// <summary>
    /// User message submitted for processing through the conversation pipeline.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Text">User's message text</param>
    /// <param name="Timestamp">Timestamp when the message was created</param>
    public record UserMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp);

    // ==========================================================================
    // GUARD (CONTENT MODERATION) MESSAGES
    // ==========================================================================

    /// <summary>
    /// Request for content moderation check on a user message.
    /// Sent to GuardActor for two-level filtering (profanity + LLM policy check).
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Message">User message to check for policy violations</param>
    public record GuardCheckRequest(
        string ConversationId,
        string Message);

    /// <summary>
    /// Result of content moderation check from GuardActor.
    /// </summary>
    /// <param name="Compliant">True if message passes policy checks, false if violation detected</param>
    /// <param name="Violation">Description of policy violation if Compliant is false</param>
    public record GuardCheckResponse(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    // ==========================================================================
    // CLASSIFICATION MESSAGES
    // ==========================================================================

    /// <summary>
    /// LLM response from ClassifierActor containing intent classification.
    /// Deserialized from JSON returned by the LLM.
    /// </summary>
    /// <param name="Intent">Classified intent name (e.g., "billing", "contract", "other")</param>
    /// <param name="Confidence">Confidence score from 0.0 to 1.0</param>
    public record ClassificationResponse(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("confidence")] double Confidence);

    /// <summary>
    /// Internal classification result used by the conversation pipeline.
    /// Contains the classified intent and additional metadata for routing and diagnostics.
    /// </summary>
    /// <param name="Intent">Classified intent name</param>
    /// <param name="Metadata">
    /// Additional metadata dictionary (e.g., confidence score, error codes, classification notes)
    /// </param>
    public record ClassificationResult(
        string Intent,
        Dictionary<string, string> Metadata);

    // ==========================================================================
    // AGENT REQUEST/RESPONSE MODELS
    // ==========================================================================

    /// <summary>
    /// Request message sent to MorganaAgent instances for processing user input.
    /// Contains the user's message and optional classification result from ClassifierActor.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Content">User message text to process (null for tool-only interactions)</param>
    /// <param name="Classification">Optional intent classification result (null for follow-up messages to active agents)</param>
    public record AgentRequest(
        string ConversationId,
        string? Content,
        ClassificationResult? Classification);

    /// <summary>
    /// Response message from MorganaAgent instances after processing a request.
    /// Indicates the agent's response text, completion status, and optional quick reply buttons.
    /// </summary>
    /// <param name="Response">Agent's response text (may contain #INT# token for multi-turn interactions)</param>
    /// <param name="IsCompleted">
    /// True if agent has completed its task (conversation returns to idle).
    /// False if agent needs more user input (agent becomes active for follow-up messages).
    /// </param>
    /// <param name="QuickReplies">
    /// Optional list of quick reply buttons to display to the user.
    /// Agents can provide guided choices for better UX (e.g., contract sections, invoice selection).
    /// If null, no quick replies are shown.
    /// </param>
    /// <remarks>
    /// <para><strong>Quick Reply Usage:</strong></para>
    /// <para>Agents can emit quick replies to guide users through complex workflows:</para>
    /// <code>
    /// // Billing agent offering invoice options
    /// new AgentResponse(
    ///     "I can help with your invoices.",
    ///     IsCompleted: false,
    ///     QuickReplies: new List&lt;QuickReply&gt; {
    ///         new("diag", "🔧 Show recent invoices", "Show me my invoices"),
    ///         new("guide", "📖 Show payment history", "Show me history of my payments")
    ///     });
    /// </code>
    /// <para><strong>Best Practices:</strong></para>
    /// <list type="bullet">
    /// <item>Use clear, action-oriented labels with emoji for visual appeal</item>
    /// <item>Set IsCompleted=false when quick replies represent continuation of workflow</item>
    /// <item>Quick reply values should be natural user messages that trigger appropriate agent behavior</item>
    /// </list>
    /// </remarks>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Response from RouterActor containing both the agent's response and a reference to the agent actor.
    /// Used to track which agent is handling the request for multi-turn conversation management.
    /// </summary>
    /// <param name="Response">Agent's response text</param>
    /// <param name="IsCompleted">Whether the agent has completed its task</param>
    /// <param name="AgentRef">Actor reference to the agent that generated this response</param>
    /// <param name="QuickReplies">Optional list of quick reply buttons from the agent</param>
    public record ActiveAgentResponse(
        string Response,
        bool IsCompleted,
        IActorRef AgentRef,
        List<QuickReply>? QuickReplies = null,
        RichCard? RichCard = null);

    /// <summary>
    /// Represents a streaming chunk from an agent during real-time response generation.
    /// Sent incrementally to enable progressive UI rendering.
    /// </summary>
    public record AgentStreamChunk(
        string Text);

    // ==========================================================================
    // AGENT COMMUNICATION MESSAGES
    // ==========================================================================

    /// <summary>
    /// Message sent by an agent to RouterActor to broadcast shared context variables to all other agents.
    /// Enables cross-agent coordination by sharing information like userId across agents.
    /// </summary>
    /// <param name="SourceAgentIntent">Intent name of the agent broadcasting the update (e.g., "billing")</param>
    /// <param name="UpdatedValues">Dictionary of variable names and values to broadcast</param>
    public record BroadcastContextUpdate(
        string SourceAgentIntent,
        Dictionary<string, object> UpdatedValues);

    /// <summary>
    /// Message sent by RouterActor to agents to notify them of shared context updates from other agents.
    /// Agents merge these updates into their local context using first-write-wins strategy.
    /// </summary>
    /// <param name="SourceAgentIntent">Intent name of the agent that originated the update</param>
    /// <param name="UpdatedValues">Dictionary of variable names and values to merge</param>
    public record ReceiveContextUpdate(
        string SourceAgentIntent,
        Dictionary<string, object> UpdatedValues);

    // ==========================================================================
    // HTTP REQUEST/RESPONSE MODELS
    // ==========================================================================

    /// <summary>
    /// HTTP request model for starting a new conversation via REST API.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the conversation to create</param>
    /// <param name="InitialContext">Optional initial context information (reserved for future use)</param>
    public record StartConversationRequest(
        string ConversationId,
        string? InitialContext = null);

    /// <summary>
    /// HTTP request model for sending a message to a conversation via REST API.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the target conversation</param>
    /// <param name="Text">Message text from the user</param>
    /// <param name="Metadata">Optional metadata dictionary (reserved for future use)</param>
    public record SendMessageRequest(
        string ConversationId,
        string Text,
        Dictionary<string, object>? Metadata = null
    );

    // ==========================================================================
    // QUICK REPLY SYSTEM
    // ==========================================================================

    /// <summary>
    /// Interactive button displayed to the user for quick action selection.
    /// Used in presentation messages, agent responses, and JSON deserialization from LLM tool calls.
    /// </summary>
    /// <param name="Id">Unique identifier for the quick reply (typically matches intent name or action)</param>
    /// <param name="Label">Display text shown on the button with emoji (e.g., "📄 View Invoices")</param>
    /// <param name="Value">Message text sent when user clicks the button (e.g., "Show my invoices")</param>
    /// <remarks>
    /// <para><strong>Dual Purpose:</strong></para>
    /// <para>This record serves both as a runtime model and JSON serialization DTO:</para>
    /// <list type="bullet">
    /// <item><term>Runtime Model</term><description>Used by AgentResponse, ConversationResponse, StructuredMessage</description></item>
    /// <item><term>JSON DTO</term><description>Deserialized from LLM SetQuickReplies tool calls</description></item>
    /// </list>
    /// <para><strong>JSON Format:</strong></para>
    /// <code>
    /// {
    ///   "id": "no-internet",
    ///   "label": "🔴 No Internet Connection",
    ///   "value": "Show me the no-internet assistance guide"
    /// }
    /// </code>
    /// </remarks>
    public record QuickReply(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("termination")] bool? Termination=false);

    /// <summary>
    /// LLM-generated presentation response from ConversationSupervisorActor.
    /// Contains the welcome message and quick reply buttons for user interaction.
    /// Deserialized from JSON returned by the LLM when generating presentation messages.
    /// </summary>
    /// <param name="Message">Welcome/presentation message text (2-4 sentences)</param>
    /// <param name="QuickReplies">List of quick reply button definitions</param>
    public record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("quickReplies")] List<QuickReply> QuickReplies);

    // ==========================================================================
    // RICH CARD SYSTEM
    // ==========================================================================
    // Add this section to Morgana.Framework/Records.cs

    /// <summary>
    /// Rich card container for structured visual presentation of complex data.
    /// Used to render information (invoices, profiles, reports) with visual hierarchy
    /// instead of plain text walls.
    /// </summary>
    /// <param name="Title">Main title of the card</param>
    /// <param name="Subtitle">Optional subtitle or secondary information</param>
    /// <param name="Components">Array of visual components to render</param>
    /// <remarks>
    /// <para><strong>Usage:</strong></para>
    /// <para>LLM generates rich cards via SetRichCard tool when presenting structured data.
    /// Cards flow through actor pipeline (Agent → Router → Supervisor → Manager → SignalR → Cauldron).</para>
    /// <para><strong>Constraints:</strong></para>
    /// <list type="bullet">
    /// <item>Maximum nesting depth: 3 levels (enforced by SetRichCard tool)</item>
    /// <item>Maximum 50 components total (prevents abuse)</item>
    /// <item>Components must be from known dictionary (unknown types fallback to text in Cauldron)</item>
    /// </list>
    /// </remarks>
    public record RichCard(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("components")] List<CardComponent> Components
    );

    /// <summary>
    /// Base class for all card components.
    /// Uses JSON polymorphic serialization for type discrimination.
    /// </summary>
    /// <remarks>
    /// <para><strong>Component Dictionary:</strong></para>
    /// <list type="bullet">
    /// <item><term>text_block</term><description>Free-form narrative text</description></item>
    /// <item><term>key_value</term><description>Label-value pairs for structured data</description></item>
    /// <item><term>divider</term><description>Visual separator between sections</description></item>
    /// <item><term>list</term><description>Bulleted, numbered, or plain item lists</description></item>
    /// <item><term>section</term><description>Nestable grouping with title/subtitle</description></item>
    /// <item><term>grid</term><description>2-4 column layout for side-by-side data</description></item>
    /// <item><term>badge</term><description>Status indicators (success, warning, error, info, neutral)</description></item>
    /// </list>
    /// <para><strong>Extensibility:</strong></para>
    /// <para>Implementers can add new component types by:</para>
    /// <list type="number">
    /// <item>Adding new record inheriting from CardComponent</item>
    /// <item>Adding JsonDerivedType attribute to CardComponent</item>
    /// <item>Creating corresponding Razor component in Cauldron</item>
    /// </list>
    /// </remarks>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(TextBlockComponent), "text_block")]
    [JsonDerivedType(typeof(KeyValueComponent), "key_value")]
    [JsonDerivedType(typeof(DividerComponent), "divider")]
    [JsonDerivedType(typeof(ListComponent), "list")]
    [JsonDerivedType(typeof(SectionComponent), "section")]
    [JsonDerivedType(typeof(GridComponent), "grid")]
    [JsonDerivedType(typeof(BadgeComponent), "badge")]
    public abstract record CardComponent;

    /// <summary>
    /// Free-form text block component for narrative content within cards.
    /// </summary>
    /// <param name="Content">Text content (supports multiline)</param>
    /// <param name="Style">Visual styling for the text</param>
    public record TextBlockComponent(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("style")] TextStyle Style = TextStyle.Normal
    ) : CardComponent;

    /// <summary>
    /// Key-value pair component for structured label-value data.
    /// </summary>
    /// <param name="Key">Label/field name (e.g., "Cliente", "Totale")</param>
    /// <param name="Value">Corresponding value (e.g., "Acme Corp", "€1.250,00")</param>
    /// <param name="Emphasize">True to highlight this pair visually (e.g., bold, larger font)</param>
    public record KeyValueComponent(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("emphasize")] bool Emphasize = false
    ) : CardComponent;

    /// <summary>
    /// Visual divider/separator component.
    /// Renders as horizontal line to separate logical sections.
    /// </summary>
    public record DividerComponent() : CardComponent;

    /// <summary>
    /// List component for displaying multiple related items.
    /// </summary>
    /// <param name="Items">Array of text items to display</param>
    /// <param name="Style">List presentation style (bullet, numbered, plain)</param>
    public record ListComponent(
        [property: JsonPropertyName("items")] List<string> Items,
        [property: JsonPropertyName("style")] ListStyle Style = ListStyle.Bullet
    ) : CardComponent;

    /// <summary>
    /// Section component for logical grouping with nesting support.
    /// Enables hierarchical organization of card content (max depth: 3).
    /// </summary>
    /// <param name="Title">Section title/heading</param>
    /// <param name="Subtitle">Optional section subtitle</param>
    /// <param name="Components">Child components within this section (can include nested sections)</param>
    public record SectionComponent(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("components")] List<CardComponent> Components
    ) : CardComponent;

    /// <summary>
    /// Grid component for side-by-side data presentation.
    /// </summary>
    /// <param name="Columns">Number of columns (2-4 recommended)</param>
    /// <param name="Items">Grid cells with key-value pairs</param>
    public record GridComponent(
        [property: JsonPropertyName("columns")] int Columns,
        [property: JsonPropertyName("items")] List<GridItem> Items
    ) : CardComponent;

    /// <summary>
    /// Individual grid cell containing a key-value pair.
    /// </summary>
    /// <param name="Key">Label for this grid cell</param>
    /// <param name="Value">Value for this grid cell</param>
    public record GridItem(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] string Value
    );

    /// <summary>
    /// Badge component for status indicators and categorical labels.
    /// </summary>
    /// <param name="Text">Badge text (e.g., "Pagata", "In sospeso")</param>
    /// <param name="Variant">Visual variant determining color/style</param>
    public record BadgeComponent(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("variant")] BadgeVariant Variant = BadgeVariant.Neutral
    ) : CardComponent;

    // Enums for component styling

    /// <summary>
    /// Text styling options for TextBlockComponent.
    /// </summary>
    public enum TextStyle
    {
        [JsonPropertyName("normal")] Normal,
        [JsonPropertyName("bold")] Bold,
        [JsonPropertyName("muted")] Muted,
        [JsonPropertyName("small")] Small
    }

    /// <summary>
    /// List presentation styles for ListComponent.
    /// </summary>
    public enum ListStyle
    {
        [JsonPropertyName("bullet")] Bullet,
        [JsonPropertyName("numbered")] Numbered,
        [JsonPropertyName("plain")] Plain
    }

    /// <summary>
    /// Badge color variants for BadgeComponent.
    /// </summary>
    public enum BadgeVariant
    {
        [JsonPropertyName("success")] Success,
        [JsonPropertyName("warning")] Warning,
        [JsonPropertyName("error")] Error,
        [JsonPropertyName("info")] Info,
        [JsonPropertyName("neutral")] Neutral
    }

    // ==========================================================================
    // PRESENTATION FLOW MESSAGES
    // ==========================================================================

    /// <summary>
    /// Trigger message to generate and send the initial presentation/welcome message.
    /// Sent automatically when a conversation is created.
    /// </summary>
    public record GeneratePresentationMessage;

    /// <summary>
    /// Context containing the generated presentation message and available intents.
    /// Used internally by ConversationSupervisorActor to send presentation via SignalR.
    /// </summary>
    /// <param name="Message">Welcome message text (either LLM-generated or fallback)</param>
    /// <param name="Intents">List of available intent definitions</param>
    public record PresentationContext(
        string Message,
        List<IntentDefinition> Intents)
    {
        /// <summary>
        /// LLM-generated quick replies (takes precedence over Intents if available).
        /// If null, quick replies are derived from Intents directly.
        /// </summary>
        public List<QuickReply>? LLMQuickReplies { get; init; }
    }

    // ==========================================================================
    // CONTEXT WRAPPERS FOR BECOME/PIPETO PATTERN
    // ==========================================================================
    // These records wrap async operation results with the original sender reference
    // to ensure correct message routing after async operations complete.

    // --- ConversationSupervisorActor Contexts ---

    /// <summary>
    /// Processing context maintained throughout the conversation pipeline.
    /// Captures the original message, sender, and classification result as it flows through states.
    /// </summary>
    /// <param name="OriginalMessage">The user message being processed</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationManagerActor)</param>
    /// <param name="Classification">Intent classification result (populated after ClassifierActor processes message)</param>
    public record ProcessingContext(
        UserMessage OriginalMessage,
        IActorRef OriginalSender,
        ClassificationResult? Classification = null);

    // --- RouterActor Contexts ---

    /// <summary>
    /// Context wrapper for failure via PipeTo in RouterActor.
    /// Captures Akka.NET failure state and original sender for proper routing.
    /// </summary>
    /// <param name="Failure">Akka.NET failure status</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record FailureContext(
        Status.Failure Failure,
        IActorRef OriginalSender);

    // ==========================================================================
    // INTENT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Intent definition for classification and presentation.
    /// Defines what intents the system can recognize and how to present them to users.
    /// </summary>
    /// <param name="Name">Intent identifier (lowercase, e.g., "billing", "contract")</param>
    /// <param name="Description">Intent description for classifier LLM</param>
    /// <param name="Label">User-facing label with emoji (e.g., "📄 Billing") for quick replies</param>
    /// <param name="DefaultValue">Sample user message for this intent (used in quick reply value)</param>
    public record IntentDefinition(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Description")] string Description,
        [property: JsonPropertyName("Label")] string? Label,
        [property: JsonPropertyName("DefaultValue")] string? DefaultValue = null);

    // ==========================================================================
    // INTENT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Collection of intent definitions with utility methods for classification and presentation.
    /// Provides filtering and formatting capabilities for different use cases.
    /// </summary>
    public record IntentCollection
    {
        /// <summary>
        /// List of all intent definitions.
        /// </summary>
        public List<IntentDefinition> Intents { get; set; }

        /// <summary>
        /// Initializes a new instance of IntentCollection.
        /// </summary>
        /// <param name="intents">List of intent definitions to wrap</param>
        public IntentCollection(List<IntentDefinition> intents)
        {
            Intents = intents;
        }

        /// <summary>
        /// Converts intents to a dictionary mapping intent names to descriptions.
        /// Used by ClassifierActor to format intents for LLM classification prompt.
        /// </summary>
        /// <returns>Dictionary with intent name as key and description as value</returns>
        /// <remarks>
        /// <para><strong>Usage in ClassifierActor:</strong></para>
        /// <code>
        /// IntentCollection intentCollection = new IntentCollection(intents);
        /// Dictionary&lt;string, string&gt; intentDict = intentCollection.AsDictionary();
        ///
        /// // Format for LLM: "billing (requests to view invoices)|contract (requests to summarize contract)"
        /// string formattedIntents = string.Join("|", intentDict.Select(kvp => $"{kvp.Key} ({kvp.Value})"));
        /// </code>
        /// </remarks>
        public Dictionary<string, string> AsDictionary()
        {
            return Intents.ToDictionary(i => i.Name, i => i.Description);
        }

        /// <summary>
        /// Gets intents that should be displayed in presentation quick replies.
        /// Excludes the "other" fallback intent and intents without labels.
        /// </summary>
        /// <returns>List of displayable intent definitions</returns>
        /// <remarks>
        /// <para><strong>Filtering Rules:</strong></para>
        /// <list type="bullet">
        /// <item>Exclude "other" intent (special fallback, not user-selectable)</item>
        /// <item>Exclude intents without Label property (not meant for UI display)</item>
        /// </list>
        /// <para><strong>Usage in Presentation:</strong></para>
        /// <code>
        /// IntentCollection intentCollection = new IntentCollection(allIntents);
        /// List&lt;IntentDefinition&gt; displayable = intentCollection.GetDisplayableIntents();
        ///
        /// // Convert to quick replies for SignalR
        /// List&lt;QuickReply&gt; quickReplies = displayable
        ///     .Select(i => new QuickReply(i.Name, i.Label, i.DefaultValue))
        ///     .ToList();
        /// </code>
        /// </remarks>
        public List<IntentDefinition> GetDisplayableIntents()
        {
            return Intents
                .Where(i => !string.Equals(i.Name, "other", StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrEmpty(i.Label))
                .ToList();
        }
    }

    // ==========================================================================
    // PROMPT CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Root collection of prompts loaded from configuration files (morgana.json, agents.json).
    /// Used during JSON deserialization.
    /// </summary>
    /// <param name="Prompts">Array of prompt definitions</param>
    public record PromptCollection(
        Prompt[] Prompts);

    /// <summary>
    /// Prompt definition containing instructions, personality, and metadata for agents and actors.
    /// Loaded from morgana.json (framework prompts) or agents.json (domain prompts).
    /// </summary>
    /// <param name="ID">
    /// Unique prompt identifier.
    /// Framework: "Morgana", "Classifier", "Guard", "Presentation"
    /// Domain: Intent names like "billing", "contract", "monkeys"
    /// </param>
    /// <param name="Type">Prompt type (e.g., "SYSTEM", "INTENT")</param>
    /// <param name="SubType">Prompt subtype (e.g., "AGENT", "ACTOR", "PRESENTATION")</param>
    /// <param name="Target">Core prompt text defining role and capabilities</param>
    /// <param name="Instructions">Behavioral rules and guidelines</param>
    /// <param name="Personality">Optional tone and character traits</param>
    /// <param name="Language">Language code (e.g., "en-US", "it-IT")</param>
    /// <param name="Version">Prompt version for tracking changes</param>
    /// <param name="AdditionalProperties">
    /// List of dictionaries containing additional data like GlobalPolicies, Tools, ErrorAnswers, etc.
    /// </param>
    public record Prompt(
        string ID,
        string Type,
        string SubType,
        string Target,
        string Instructions,
        string Formatting,
        string? Personality,
        string Language,
        string Version,
        List<Dictionary<string, object>> AdditionalProperties)
    {
        /// <summary>
        /// Gets a strongly-typed additional property from the prompt configuration.
        /// Searches all AdditionalProperties dictionaries for the specified key.
        /// </summary>
        /// <typeparam name="T">Type to deserialize the property value into</typeparam>
        /// <param name="additionalPropertyName">Name of the property to retrieve (e.g., "Tools", "GlobalPolicies")</param>
        /// <returns>Deserialized property value</returns>
        /// <exception cref="KeyNotFoundException">Thrown if property not found in any AdditionalProperties dictionary</exception>
        /// <remarks>
        /// <para><strong>Common Additional Properties:</strong></para>
        /// <list type="bullet">
        /// <item><term>Tools</term><description>List&lt;ToolDefinition&gt; - Tool configurations for agents</description></item>
        /// <item><term>GlobalPolicies</term><description>List&lt;GlobalPolicy&gt; - Framework-level behavioral policies</description></item>
        /// <item><term>ErrorAnswers</term><description>List&lt;ErrorAnswer&gt; - Error message templates</description></item>
        /// <item><term>ProfanityTerms</term><description>List&lt;string&gt; - Terms for content moderation</description></item>
        /// <item><term>FallbackMessage</term><description>string - Default presentation message</description></item>
        /// </list>
        /// <para><strong>Usage Examples:</strong></para>
        /// <code>
        /// Prompt morganaPrompt = await promptResolver.ResolveAsync("Morgana");
        ///
        /// // Get global policies
        /// List&lt;GlobalPolicy&gt; policies = morganaPrompt.GetAdditionalProperty&lt;List&lt;GlobalPolicy&gt;&gt;("GlobalPolicies");
        ///
        /// // Get tools
        /// ToolDefinition[] tools = morganaPrompt.GetAdditionalProperty&lt;ToolDefinition[]&gt;("Tools");
        ///
        /// // Get error messages
        /// List&lt;ErrorAnswer&gt; errors = morganaPrompt.GetAdditionalProperty&lt;List&lt;ErrorAnswer&gt;&gt;("ErrorAnswers");
        /// </code>
        /// </remarks>
        public T GetAdditionalProperty<T>(string additionalPropertyName)
        {
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
            {
                if (additionalProperties.TryGetValue(additionalPropertyName, out object value))
                {
                    JsonElement element = (JsonElement)value;
                    return element.Deserialize<T>();
                }
            }
            throw new KeyNotFoundException($"AdditionalProperty with key '{additionalPropertyName}' was not found in the prompt with id='{ID}'");
        }
    }

    /// <summary>
    /// Global policy definition specifying framework-level behavioral rules.
    /// Applied to all agents and actors to enforce consistent behavior.
    /// </summary>
    /// <param name="Name">Policy name (e.g., "ContextHandling", "InteractiveToken")</param>
    /// <param name="Description">Detailed policy description with enforcement rules</param>
    /// <param name="Type">Policy type ("Critical" or "Operational")</param>
    /// <param name="Priority">Priority level (lower number = higher priority)</param>
    public record GlobalPolicy(
        string Name,
        string Description,
        string Type,
        int Priority);

    /// <summary>
    /// Error message template with named identifier.
    /// Used to provide consistent, user-friendly error messages across the system.
    /// </summary>
    /// <param name="Name">Error identifier (e.g., "GenericError", "LLMServiceError")</param>
    /// <param name="Content">Error message template (may contain placeholders like ((llm_error)))</param>
    public record ErrorAnswer(
        string Name,
        string Content);

    // ==========================================================================
    // TOOL CONFIGURATION RECORDS
    // ==========================================================================

    /// <summary>
    /// Tool definition specifying a callable tool method with parameters.
    /// Loaded from agents.json and used by MorganaToolAdapter to create AIFunction instances.
    /// </summary>
    /// <param name="Name">Tool method name (must match actual method name in MorganaTool class)</param>
    /// <param name="Description">Tool description for LLM understanding</param>
    /// <param name="Parameters">List of tool parameter definitions</param>
    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters);

    /// <summary>
    /// Tool parameter definition specifying parameter name, description, and behavior.
    /// Controls whether parameter comes from context or request, and if it's shared across agents.
    /// </summary>
    /// <param name="Name">Parameter name (must match method parameter name)</param>
    /// <param name="Description">Parameter description for LLM understanding</param>
    /// <param name="Required">Whether the parameter is required (true) or optional (false)</param>
    /// <param name="Scope">
    /// Parameter scope: "context" (retrieve via GetContextVariable) or "request" (use directly from user input)
    /// </param>
    /// <param name="Shared">
    /// Whether this context variable should be broadcast to other agents.
    /// Only applies when Scope="context". Default: false.
    /// </param>
    public record ToolParameter(
        string Name,
        string Description,
        bool Required,
        string Scope,
        bool Shared = false);

    // ==========================================================================
    // MODEL CONTEXT PROTOCOL
    // ==========================================================================

    /// <summary>
    /// MCP server configuration from appsettings.json.
    /// Defines how to connect to and initialize MCP servers.
    /// </summary>
    public record MCPServerConfig(
        string Name,
        string Uri,
        bool Enabled,
        Dictionary<string, string>? AdditionalSettings = null);
}