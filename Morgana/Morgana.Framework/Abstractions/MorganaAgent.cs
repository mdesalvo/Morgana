using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;
using Morgana.Framework.Providers;
using System.Reflection;
using System.Text.Json;

namespace Morgana.Framework.Abstractions;

/// <summary>
/// Base class for domain-specific conversational agents in the Morgana framework.
/// Extends MorganaActor with AI agent capabilities, conversation threading, context management, and inter-agent communication.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>MorganaAgent provides the foundation for building specialized conversational agents that handle specific intents
/// (e.g., BillingAgent, ContractAgent, TroubleshootingAgent). Each agent manages its own conversation thread,
/// context variables, and can communicate with other agents via the RouterActor.</para>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item><term>AI Agent Integration</term><description>Uses Microsoft.Agents.AI for LLM interactions with tool calling</description></item>
/// <item><term>Conversation Threading</term><description>Maintains conversation history via AgentThread for context-aware responses</description></item>
/// <item><term>Context Management</term><description>MorganaContextProvider for reading/writing conversation variables</description></item>
/// <item><term>Inter-Agent Communication</term><description>Broadcast and receive shared context variables across agents</description></item>
/// <item><term>Interactive Token Detection</term><description>Detects #INT# token to signal multi-turn conversations</description></item>
/// </list>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaActor
///   └── MorganaAgent (adds AI agent capabilities)
///         ├── BillingAgent [HandlesIntent("billing")]
///         ├── ContractAgent [HandlesIntent("contract")]
///         └── TroubleshootingAgent [HandlesIntent("troubleshooting")]
/// </code>
/// <para><strong>Usage Pattern:</strong></para>
/// <para>Agents receive AgentRequest messages, process them via ExecuteAgentAsync, and respond with AgentResponse.
/// The #INT# token in responses signals incomplete processing, causing the supervisor to route follow-up messages
/// directly to the same agent.</para>
/// </remarks>
public class MorganaAgent : MorganaActor
{
    /// <summary>
    /// Microsoft.Agents.AI agent instance for LLM interactions with tool calling support.
    /// Configured with agent-specific prompts, personality, and available tools.
    /// </summary>
    protected AIAgent aiAgent;

    /// <summary>
    /// Conversation thread maintaining the history of messages with this agent.
    /// Created lazily on first agent execution and reused for follow-up messages.
    /// </summary>
    protected AgentThread? aiAgentThread;

    /// <summary>
    /// Context provider for reading and writing conversation variables.
    /// Manages both local (agent-specific) and shared (cross-agent) context.
    /// </summary>
    protected MorganaContextProvider contextProvider;

    /// <summary>
    /// Service for persisting and loading conversation state (AgentThread + context) across application restarts.
    /// Enables resuming conversations from encrypted file storage.
    /// </summary>
    protected readonly IConversationPersistenceService persistenceService;

    /// <summary>
    /// Logger instance for agent-level logging (separate from actorLogger).
    /// Uses Microsoft.Extensions.Logging for consistency with agent framework.
    /// </summary>
    protected readonly ILogger agentLogger;

    /// <summary>
    /// Gets the intent handled by this agent instance.
    /// Extracted from the HandlesIntentAttribute decorating the agent class.
    /// </summary>
    /// <remarks>
    /// <para><strong>Example:</strong> "billing", "contract", "troubleshooting"</para>
    /// <para><strong>Important:</strong> This property throws if HandlesIntentAttribute is not present.
    /// All MorganaAgent subclasses MUST be decorated with [HandlesIntent("...")] attribute.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if HandlesIntentAttribute is not present on the agent class</exception>
    protected string AgentIntent => GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent
                                     ?? throw new InvalidOperationException($"Agent {GetType().Name} must be decorated with [HandlesIntent] attribute");

    /// <summary>
    /// Gets the agent-specific conversation identifier combining intent and conversationId.
    /// Used for persistence to ensure each agent type has its own isolated thread storage.
    /// </summary>
    /// <remarks>
    /// <para><strong>Format:</strong> {intent}-{conversationId}</para>
    /// <para><strong>Example:</strong> "billing-conv_123", "contract-conv_456"</para>
    /// <para><strong>Why Needed:</strong></para>
    /// <para>In Morgana, each agent maintains its own AgentThread within a conversation.
    /// BillingAgent, ContractAgent, and MonkeyAgent all participate in the same
    /// conversation but have separate message histories and context. This property ensures
    /// each agent's state is persisted to a separate file.</para>
    /// <code>
    /// Conversation: "conv_123"
    ///   ├─ BillingAgent     → billing-conv_123.morgana.json
    ///   ├─ ContractAgent    → contract-conv_123.morgana.json
    ///   └─ TroubleshootAgent → troubleshoot-conv_123.morgana.json
    /// </code>
    /// </remarks>
    protected string AgentIdentifier => $"{AgentIntent}-{conversationId}";

    /// <summary>
    /// Initializes a new instance of MorganaAgent with AI agent infrastructure.
    /// Sets up conversation threading, context management, and inter-agent communication.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation this agent will handle</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="persistenceService">Service for persisting conversation state to encrypted storage</param>
    /// <param name="agentLogger">Logger instance for agent-level diagnostics</param>
    public MorganaAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger agentLogger) : base(conversationId, llmService, promptResolverService)
    {
        this.persistenceService = persistenceService;
        this.agentLogger = agentLogger;

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
        Receive<Records.ReceiveContextUpdate>(HandleContextUpdate);
    }

    /// <summary>
    /// Deserializes a previously serialized AgentThread, restoring conversation history and context state.
    /// Recreates the MorganaContextProvider from persisted state and reconnects shared context callbacks.
    /// </summary>
    /// <param name="serializedThread">JSON element containing the serialized thread state from a previous Serialize() call</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (defaults to AgentAbstractionsJsonUtilities.DefaultOptions)</param>
    /// <returns>Fully reconstituted AgentThread with restored message history, context variables, and chat message store</returns>
    /// <remarks>
    /// <para><strong>Deserialization Process:</strong></para>
    /// <list type="number">
    /// <item>Extract AIContextProviderState from serialized thread JSON</item>
    /// <item>Create new MorganaContextProvider instance using deserialization constructor</item>
    /// <item>Reconnect OnSharedContextUpdate callback for inter-agent communication</item>
    /// <item>Delegate to underlying AIAgent.DeserializeThread to restore message history and chat store</item>
    /// <item>Return fully functional AgentThread ready to continue the conversation</item>
    /// </list>
    /// <para><strong>Usage Pattern:</strong></para>
    /// <code>
    /// // Serialize thread for persistence
    /// JsonElement serialized = aiAgentThread.Serialize();
    /// await database.SaveConversation(conversationId, JsonSerializer.Serialize(serialized));
    ///
    /// // Later: deserialize thread to resume conversation
    /// string savedJson = await database.LoadConversation(conversationId);
    /// JsonElement loaded = JsonSerializer.Deserialize&lt;JsonElement&gt;(savedJson);
    /// AgentThread restored = DeserializeThread(loaded);
    ///
    /// // Continue conversation with restored context
    /// var response = await aiAgent.RunAsync("What was my previous question?", restored);
    /// </code>
    /// <para><strong>State Restoration:</strong></para>
    /// <para>This method restores all conversation state including:</para>
    /// <list type="bullet">
    /// <item>Message history (user and assistant messages)</item>
    /// <item>Context variables (both private and shared)</item>
    /// <item>Shared variable names configuration</item>
    /// <item>Chat message store state</item>
    /// </list>
    /// <para><strong>Important:</strong></para>
    /// <para>The deserialized thread is automatically assigned to aiAgentThread field, so subsequent
    /// ExecuteAgentAsync calls will use the restored conversation context.</para>
    /// </remarks>
    public virtual AgentThread DeserializeThread(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // Extract AIContextProviderState if present
        JsonElement providerState = default;
        if (serializedThread.TryGetProperty("aiContextProviderState", out JsonElement stateElement))
            providerState = stateElement;

        // Recreate context provider from serialized state
        MorganaContextProvider restoredProvider = new MorganaContextProvider(
            agentLogger,
            providerState,
            jsonSerializerOptions);

        // Reconnect shared context update callback
        restoredProvider.OnSharedContextUpdate = OnSharedContextUpdate;

        // Propagate shared variables with connected callback
        restoredProvider.PropagateSharedVariables();

        // Assign restored provider
        contextProvider = restoredProvider;

        // Delegate to underlying AIAgent to deserialize thread
        aiAgentThread = aiAgent.DeserializeThread(serializedThread, jsonSerializerOptions);

        agentLogger.LogInformation($"Deserialized AgentThread for conversation {conversationId}");

        return aiAgentThread;
    }

    /// <summary>
    /// Callback invoked when a tool sets a shared context variable.
    /// Broadcasts the variable to all other agents via RouterActor for cross-agent coordination.
    /// </summary>
    /// <param name="key">Name of the shared context variable (e.g., "userId")</param>
    /// <param name="value">Value of the shared context variable</param>
    /// <remarks>
    /// <para><strong>Shared Context Pattern:</strong></para>
    /// <para>When an agent discovers important information (e.g., userId from BillingAgent), it can broadcast
    /// that information to all other agents so they don't need to ask the user again.</para>
    /// <para><strong>Example Flow:</strong></para>
    /// <list type="number">
    /// <item>BillingAgent tool calls SetContextVariable("userId", "P994E", shared: true)</item>
    /// <item>MorganaContextProvider detects shared variable and calls OnSharedContextUpdate</item>
    /// <item>Agent broadcasts to RouterActor via BroadcastContextUpdate message</item>
    /// <item>RouterActor sends ReceiveContextUpdate to all other agents</item>
    /// <item>ContractAgent receives userId and can use it without asking user</item>
    /// </list>
    /// <para><strong>Actor Selection:</strong></para>
    /// <para>Uses actor selection pattern to find RouterActor at /user/router-{conversationId}.
    /// This is a fire-and-forget Tell operation (no response expected).</para>
    /// </remarks>
    protected void OnSharedContextUpdate(string key, object value)
    {
        agentLogger.LogInformation($"Agent {AgentIntent} broadcasting shared context variable: {key}");

        Context.ActorSelection($"/user/router-{conversationId}")
            .Tell(new Records.BroadcastContextUpdate(
                AgentIntent,
                new Dictionary<string, object> { [key] = value }
            ));
    }

    /// <summary>
    /// Handles context update broadcasts from other agents via RouterActor.
    /// Implements intelligent merge: accepts only variables not already present (first-write-wins).
    /// </summary>
    /// <param name="msg">Context update message containing source agent intent and updated variables</param>
    /// <remarks>
    /// <para><strong>First-Write-Wins Strategy:</strong></para>
    /// <para>If an agent already has a value for a variable, it keeps its own value and ignores the broadcast.
    /// This prevents conflicts when multiple agents independently discover the same information.</para>
    /// <para><strong>Example Scenario:</strong></para>
    /// <code>
    /// // BillingAgent discovers userId
    /// BillingAgent broadcasts: { "userId": "P994E" }
    ///
    /// // ContractAgent receives broadcast and merges (first write)
    /// ContractAgent context: { "userId": "P994E" }
    ///
    /// // Later, TroubleshootingAgent independently asks user
    /// TroubleshootingAgent has: { "userId": "P994E" } (already set, ignores any conflicting broadcast)
    /// </code>
    /// </remarks>
    private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    {
        agentLogger.LogInformation(
            $"Agent '{AgentIntent}' received shared context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        contextProvider.MergeSharedContext(msg.UpdatedValues);
    }

    /// <summary>
    /// Executes the agent using AgentThread for automatic conversation history and context management.
    /// Handles conversation persistence, LLM interactions, interactive token detection, error handling, and response formatting.
    /// </summary>
    /// <param name="req">Agent request containing the user's message and optional classification</param>
    /// <returns>Task representing the async agent execution</returns>
    /// <remarks>
    /// <para><strong>Execution Flow:</strong></para>
    /// <list type="number">
    /// <item>Load existing AgentThread from encrypted storage (if exists) or create new thread</item>
    /// <item>Send user message to LLM via aiAgent.RunAsync</item>
    /// <item>Detect #INT# token in response (signals incomplete interaction)</item>
    /// <item>Detect quick replies which may have emitted by the LLM during tool's execution</item>
    /// <item>Save updated AgentThread to encrypted storage</item>
    /// <item>Remove #INT# token from production responses (kept in debug for testing)</item>
    /// <item>Send AgentResponse with completion flag to supervisor</item>
    /// </list>
    /// <para><strong>Conversation Persistence:</strong></para>
    /// <para>Every turn is automatically persisted to an encrypted .morgana.json file named by conversationId.
    /// This enables resuming conversations across application restarts with full context and message history.</para>
    /// <code>
    /// Turn 1: User asks about billing
    /// → Load: No file exists, create new thread
    /// → LLM processes, context saved
    /// → Save: conversationId.morgana.json created with encrypted state
    ///
    /// [Application restart]
    ///
    /// Turn 2: User provides customer ID
    /// → Load: conversationId.morgana.json decrypted and restored
    /// → Agent remembers previous context
    /// → LLM continues conversation seamlessly
    /// → Save: Updated state persisted
    /// </code>
    /// <para><strong>Interactive Token (#INT#):</strong></para>
    /// <para>Agents can emit "#INT#" in their responses to signal they need more information from the user.
    /// This causes the supervisor to mark the agent as "active" and route follow-up messages directly to it.</para>
    /// <code>
    /// Agent: "To help you with billing, could you provide your customer ID? #INT#"
    /// → Supervisor marks agent as active (IsCompleted = false)
    /// → User responds: "My ID is P994E"
    /// → Supervisor routes directly back to BillingAgent (skips classification)
    /// Agent: "Thank you! Here are your invoices..." (no #INT#)
    /// → Supervisor clears active agent (IsCompleted = true)
    /// </code>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>On exception, returns a generic error message from Morgana prompt configuration.
    /// Always marks as completed (IsCompleted = true) to prevent stuck conversations.</para>
    /// </remarks>
    protected async Task ExecuteAgentAsync(Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;
        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");

        try
        {
            // Load existing agent's conversation thread from encrypted storage, or create new thread
            aiAgentThread ??= await persistenceService.LoadAgentConversationAsync(AgentIdentifier, this);
            if (aiAgentThread != null)
            {
                agentLogger.LogInformation($"Loaded existing conversation thread for {AgentIdentifier}");
            }
            else
            {
                aiAgentThread = aiAgent.GetNewThread();
                agentLogger.LogInformation($"Created new conversation thread for {AgentIdentifier}");
            }

            // Execute LLM with conversation history
            AgentRunResponse llmResponse = await aiAgent.RunAsync(req.Content!, aiAgentThread);
            string llmResponseText = llmResponse.Text;

            // Detect if LLM has emitted the special token for continuing the multi-turn conversation
            bool hasInteractiveToken = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);

            // Additional multi-turn heuristic: if agent is ending with a direct question with "?",
            // may it be a "polite" way of engaging the user in the continuation of this intent,
            // or an intentional question finalized to obtain further informations
            bool endsWithQuestion = llmResponseText.EndsWith('?');

            // Retrieve quick replies from tools (if any tools set them during execution)
            List<Records.QuickReply>? quickReplies = GetQuickRepliesFromContext();
            bool hasQuickReplies = quickReplies?.Count > 0;

            // Request is completed when no further user engagement has been requested.
            // If agent offers QuickReplies, it MUST remain active to handle clicks
            // Otherwise, clicks would go through Classifier and risk "other" intent fallback
            bool isCompleted = !hasInteractiveToken && !endsWithQuestion && !hasQuickReplies;

            agentLogger.LogInformation(
                $"Agent response analysis: HasINT={hasInteractiveToken}, EndsWithQuestion={endsWithQuestion}, HasQR={hasQuickReplies}, IsCompleted={isCompleted}");

            // Persist updated agent's conversation state to encrypted storage
            await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgentThread, isCompleted);
            agentLogger.LogInformation($"Saved conversation state for {AgentIdentifier}");

            #if DEBUG
                senderRef.Tell(new Records.AgentResponse(llmResponseText, isCompleted, quickReplies));
            #else
                senderRef.Tell(new Records.AgentResponse(llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim(), isCompleted, quickReplies));
            #endif
        }
        catch (Exception ex)
        {
            agentLogger.LogError(ex, $"Error in {GetType().Name}");

            List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
            Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "GenericError", StringComparison.OrdinalIgnoreCase));

            senderRef.Tell(new Records.AgentResponse(genericError?.Content ?? "An internal error occurred.", true, null));
        }
    }

    /// <summary>
    /// Retrieves quick replies that tools may have generated during LLM execution.
    /// Tools set quick replies via SetPendingQuickReplies() when they want to guide user interactions.
    /// </summary>
    /// <returns>List of quick reply buttons from tools, or null if no tools set quick replies</returns>
    /// <remarks>
    /// <para><strong>Tool-Driven Quick Replies:</strong></para>
    /// <para>Tools know their domain data and available operations, making them the best source for
    /// contextual quick reply suggestions. This method retrieves quick replies from MorganaTool instances.</para>
    /// <para><strong>Example Flow:</strong></para>
    /// <code>
    /// 1. LLM calls "ListTroubleshootingGuides" tool
    /// 2. Tool sets quick replies for guide selection
    /// 3. Tool returns guide list text
    /// 4. Agent calls GetQuickRepliesFromContext() after tool execution
    /// 5. Agent includes quick replies in AgentResponse
    /// 6. UI displays buttons to user
    /// </code>
    /// </remarks>
    protected List<Records.QuickReply>? GetQuickRepliesFromContext()
    {
        // Retrieve from context (ContextProvider stores as JSON string)
        string? quickRepliesJson = contextProvider.GetVariable("quick_replies") as string;
        if (!string.IsNullOrEmpty(quickRepliesJson))
        {
            try
            {
                // Deserialize JSON string to List<QuickReply>
                List<Records.QuickReply>? quickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(quickRepliesJson);
                if (quickReplies != null && quickReplies.Any())
                {
                    agentLogger.LogInformation($"Retrieved {quickReplies.Count} quick replies from context");

                    // Drop after retrieval to prevent stale buttons
                    contextProvider.DropVariable("quick_replies");

                    return quickReplies;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize quick replies from context");

                // Clear corrupted data
                contextProvider.DropVariable("quick_replies");
            }
        }

        return null;
    }
}