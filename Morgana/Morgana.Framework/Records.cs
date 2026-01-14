using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;

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
    /// Confirmation message sent after a conversation is successfully created.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the created conversation</param>
    public record ConversationCreated(
        string ConversationId);

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
    public record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null,
        bool AgentCompleted = false,
        List<QuickReply>? QuickReplies = null);

    /// <summary>
    /// Request to create a new conversation and initialize the actor hierarchy.
    /// </summary>
    /// <param name="ConversationId">Unique identifier for the new conversation</param>
    public record CreateConversation(
        string ConversationId);

    /// <summary>
    /// Request to terminate a conversation and stop all associated actors.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation to terminate</param>
    public record TerminateConversation(
        string ConversationId);

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
    /// Agents can provide guided choices for better UX (e.g., troubleshooting options, invoice selection).
    /// If null, no quick replies are shown.
    /// </param>
    /// <remarks>
    /// <para><strong>Quick Reply Usage:</strong></para>
    /// <para>Agents can emit quick replies to guide users through complex workflows:</para>
    /// <code>
    /// // Troubleshooting agent offering diagnostic options
    /// new AgentResponse(
    ///     "I can help diagnose your issue.",
    ///     IsCompleted: false,
    ///     QuickReplies: new List&lt;QuickReply&gt; {
    ///         new("diag", "🔧 Run Diagnostics", "Run network diagnostics"),
    ///         new("guide", "📖 Troubleshooting Guide", "Show me troubleshooting guides")
    ///     });
    /// </code>
    /// <para><strong>Best Practices:</strong></para>
    /// <list type="bullet">
    /// <item>Limit to 3-5 quick replies per message (UI constraint)</item>
    /// <item>Use clear, action-oriented labels with emoji for visual appeal</item>
    /// <item>Set IsCompleted=false when quick replies represent continuation of workflow</item>
    /// <item>Quick reply values should be natural user messages that trigger appropriate agent behavior</item>
    /// </list>
    /// </remarks>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true,
        List<QuickReply>? QuickReplies = null);

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
        List<QuickReply>? QuickReplies = null);

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
    ///   "value": "Show me the no-internet troubleshooting guide"
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

    /// <summary>
    /// Structured message sent to clients via SignalR with full metadata support.
    /// Supports different message types, quick replies, error codes, and agent information.
    /// </summary>
    /// <param name="ConversationId">Unique identifier of the conversation</param>
    /// <param name="Text">Message text content</param>
    /// <param name="Timestamp">Timestamp when the message was created</param>
    /// <param name="MessageType">Type for client-side rendering ("assistant", "presentation", "system", "error")</param>
    /// <param name="QuickReplies">Optional list of interactive buttons for user</param>
    /// <param name="ErrorReason">Optional error code for error messages (e.g., "llm_error", "timeout")</param>
    /// <param name="AgentName">Optional name of the agent that generated the message</param>
    /// <param name="AgentCompleted">Flag indicating if the agent completed its task</param>
    public record StructuredMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp,
        string MessageType,
        List<QuickReply>? QuickReplies = null,
        string? ErrorReason = null,
        string? AgentName = null,
        bool AgentCompleted = false);

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

    /// <summary>
    /// Context wrapper for GuardActor response via PipeTo.
    /// Preserves the original processing context alongside the guard check result.
    /// </summary>
    /// <param name="Response">Guard check result (compliant or violation)</param>
    /// <param name="Context">Original processing context</param>
    public record GuardCheckContext(
        GuardCheckResponse Response,
        ProcessingContext Context);

    /// <summary>
    /// Context wrapper for ClassifierActor response via PipeTo.
    /// Preserves the original processing context alongside the classification result.
    /// This record is shared between supervisor (needing context) and router (needing sender).
    /// </summary>
    /// <param name="Classification">Intent classification result</param>
    /// <param name="Context">Original processing context</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record ClassificationContext(
        ClassificationResult Classification,
        ProcessingContext? Context,
        IActorRef? OriginalSender);

    /// <summary>
    /// Context wrapper for RouterActor/Agent response via PipeTo.
    /// Preserves the original processing context alongside the agent's response.
    /// </summary>
    /// <param name="Response">Agent response (can be ActiveAgentResponse or AgentResponse)</param>
    /// <param name="Context">Original processing context</param>
    public record AgentContext(
        object Response,
        ProcessingContext Context);

    /// <summary>
    /// Context wrapper for active agent follow-up response via PipeTo.
    /// Used when routing subsequent messages directly to an active agent.
    /// </summary>
    /// <param name="Response">Agent's follow-up response</param>
    /// <param name="OriginalSender">Actor reference to reply to</param>
    public record FollowUpContext(
        AgentResponse Response,
        IActorRef OriginalSender);

    /// <summary>
    /// Context wrapper for ConversationSupervisorActor response via PipeTo.
    /// Wraps the final conversation response for delivery to ConversationManagerActor.
    /// </summary>
    /// <param name="Response">Final conversation response with AI message and metadata</param>
    public record SupervisorResponseContext(
        ConversationResponse Response);

    // --- RouterActor Contexts ---

    /// <summary>
    /// Context wrapper for specialized agent response via PipeTo in RouterActor.
    /// Captures agent reference, response, and original sender for proper routing.
    /// </summary>
    /// <param name="Response">Agent's response</param>
    /// <param name="AgentRef">Reference to the agent that generated the response</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record AgentResponseContext(
        AgentResponse Response,
        IActorRef AgentRef,
        IActorRef OriginalSender);

    // --- GuardActor Contexts ---

    /// <summary>
    /// Context wrapper for LLM policy check response via PipeTo in GuardActor.
    /// Preserves the original sender reference across the async LLM call.
    /// </summary>
    /// <param name="Response">LLM policy check result</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record LLMCheckContext(
        GuardCheckResponse Response,
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
    /// Domain: Intent names like "billing", "contract", "troubleshooting"
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