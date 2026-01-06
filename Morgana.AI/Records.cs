using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;

namespace Morgana.AI;

/// <summary>
/// Central repository of immutable record types (DTOs) used throughout the Morgana.AI framework.
/// Records are organized by functional area: agent communication, classification, prompts, tools, and presentation.
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
    // AGENT COMMUNICATION MESSAGES
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
    /// Indicates the agent's response text and whether the agent has completed its task.
    /// </summary>
    /// <param name="Response">Agent's response text (may contain #INT# token for multi-turn interactions)</param>
    /// <param name="IsCompleted">
    /// True if agent has completed its task (conversation returns to idle).
    /// False if agent needs more user input (agent becomes active for follow-up messages).
    /// </param>
    public record AgentResponse(
        string Response,
        bool IsCompleted = true);

    /// <summary>
    /// Response from RouterActor containing both the agent's response and a reference to the agent actor.
    /// Used to track which agent is handling the request for multi-turn conversation management.
    /// </summary>
    /// <param name="Response">Agent's response text</param>
    /// <param name="IsCompleted">Whether the agent has completed its task</param>
    /// <param name="AgentRef">Actor reference to the agent that generated this response</param>
    public record ActiveAgentResponse(
        string Response,
        bool IsCompleted,
        IActorRef AgentRef);

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

    /// <summary>
    /// Context wrapper for ClassifierActor response via PipeTo.
    /// Preserves the original sender reference across async classification operation.
    /// </summary>
    /// <param name="Result">Classification result from LLM</param>
    /// <param name="OriginalSender">Actor reference to reply to (typically ConversationSupervisorActor)</param>
    public record ClassificationContext(
        ClassificationResult Result,
        IActorRef OriginalSender);

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
    /// Domain: Intent names like "billing", "contract", "troubleshooting", ...
    /// </param>
    /// <param name="Type">Prompt type (e.g., "SYSTEM", "INTENT")</param>
    /// <param name="SubType">Prompt subtype (e.g., "AGENT", "ACTOR", "PRESENTATION")</param>
    /// <param name="Content">Core prompt text defining role and capabilities</param>
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
        string Content,
        string Instructions,
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
    /// Loaded from agents.json and used by ToolAdapter to create AIFunction instances.
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
    // PRESENTATION FLOW RECORDS
    // ==========================================================================

    /// <summary>
    /// LLM-generated presentation response from ConversationSupervisorActor.
    /// Contains the welcome message and quick reply buttons for user interaction.
    /// </summary>
    /// <param name="Message">Welcome/presentation message text (2-4 sentences)</param>
    /// <param name="QuickReplies">List of quick reply button definitions</param>
    public record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("quickReplies")] List<QuickReplyDefinition> QuickReplies);

    /// <summary>
    /// Quick reply button definition for user interaction.
    /// Used in presentation messages to guide users to available capabilities.
    /// </summary>
    /// <param name="Id">Unique identifier (typically matches intent name)</param>
    /// <param name="Label">Display text on button with emoji (e.g., "📄 View Invoices")</param>
    /// <param name="Value">Message text sent when user clicks button (e.g., "Show my invoices")</param>
    public record QuickReplyDefinition(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value);
}