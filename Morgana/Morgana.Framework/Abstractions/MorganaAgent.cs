using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.Framework.Attributes;
using Morgana.Framework.Interfaces;
using Morgana.Framework.Providers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Morgana.Framework.Abstractions;

/// <summary>
/// Base class for domain-specific conversational agents in the Morgana framework.
/// Extends MorganaActor with AI agent capabilities, conversation session, context management and inter-agent communication.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>MorganaAgent provides the foundation for building specialized conversational agents that handle specific intents
/// (e.g., BillingAgent, ContractAgent, MonkeysAgent). Each agent manages its own conversation session,
/// context variables, and can communicate with other agents via the RouterActor.</para>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item><term>AI Agent Integration</term><description>Uses Microsoft.Agents.AI for LLM interactions with tool calling</description></item>
/// <item><term>Conversation Session</term><description>Maintains conversation history via AgentSession for context-aware responses</description></item>
/// <item><term>Context Management</term><description>MorganaAIContextProvider for reading/writing conversation variables</description></item>
/// <item><term>Inter-Agent Communication</term><description>Broadcast and receive shared context variables across agents</description></item>
/// <item><term>Interactive Token Detection</term><description>Detects #INT# token to signal multi-turn conversations</description></item>
/// </list>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaActor
///   └── MorganaAgent (adds AI agent capabilities)
///         ├── BillingAgent [HandlesIntent("billing")]
///         ├── ContractAgent [HandlesIntent("contract")]
///         └── MonkeysAgent [HandlesIntent("monkeys")]
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
    protected AgentSession? aiAgentSession;

    /// <summary>
    /// AI context provider for reading and writing conversation variables.
    /// Manages both local (agent-specific) and shared (cross-agent) context.
    /// </summary>
    protected MorganaAIContextProvider aiContextProvider;

    /// <summary>
    /// Service for persisting and loading conversation state (AgentSession + context) across application restarts.
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
    /// <para><strong>Example:</strong> "billing", "contract", "monkeys"</para>
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
    /// <para>In Morgana, each agent maintains its own AgentSession within a conversation.
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
        ReceiveAsync<Records.FailureContext>(HandleAgentFailureAsync);
    }

    /// <summary>
    /// Deserializes a previously serialized AgentSession, restoring conversation history and context state.
    /// Updates the existing AI context provider instance to preserve tool closures.
    /// </summary>
    /// <param name="serializedSession">JSON element containing the serialized session state from a previous Serialize() call</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (defaults to AgentAbstractionsJsonUtilities.DefaultOptions)</param>
    /// <returns>Fully reconstituted AgentSession with restored message history and context variables</returns>
    /// <remarks>
    /// <para><strong>Deserialization Process:</strong></para>
    /// <list type="number">
    /// <item>Extract AIContextProviderState from serialized session JSON</item>
    /// <item>Restore state into EXISTING MorganaAIContextProvider instance (preserves tool closures)</item>
    /// <item>Reconnect OnSharedContextUpdate callback for inter-agent communication</item>
    /// <item>Propagate shared variables to other agents</item>
    /// <item>Delegate to underlying AIAgent.DeserializeSessionAsync to restore message history</item>
    /// <item>Return fully functional AgentSession ready to continue the conversation</item>
    /// </list>
    /// <para><strong>CRITICAL - Tool Closure Preservation:</strong></para>
    /// <para>This method UPDATES the existing contextProvider instance instead of creating a new one.
    /// This is essential because tools are created with closures that capture the contextProvider field:
    /// <code>
    /// MorganaTool baseTool = new MorganaTool(logger, () => contextProvider);
    /// </code></para>
    /// <para>If we replaced the field with a new instance, tools would write to the old instance
    /// while the agent reads from the new one, causing quick_replies and other ephemeral data to be lost.</para>
    /// </remarks>
    public virtual async Task<AgentSession> DeserializeSessionAsync(
        JsonElement serializedSession,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // Extract AIContextProviderState
        JsonElement aiContextProviderState = default;
        if (serializedSession.TryGetProperty("aiContextProviderState", out JsonElement stateElement))
            aiContextProviderState = stateElement;

        // Use it to restore internal state of MorganaAIContextProvider
        if (aiContextProviderState.ValueKind != JsonValueKind.Undefined)
            aiContextProvider.RestoreState(aiContextProviderState, jsonSerializerOptions);

        // Reconnect shared context update callback
        aiContextProvider.OnSharedContextUpdate = OnSharedContextUpdate;

        // Propagate shared variables with connected callback
        aiContextProvider.PropagateSharedVariables();

        // Delegate to underlying AIAgent to complete session deserialization
        aiAgentSession = await aiAgent.DeserializeSessionAsync(serializedSession, jsonSerializerOptions);

        agentLogger.LogInformation($"Deserialized AgentSession for conversation {conversationId}");

        return aiAgentSession;
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
    /// <item>MorganaAIContextProvider detects shared variable and calls OnSharedContextUpdate</item>
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
    /// // Later, ContractAgent independently asks user
    /// ContractAgent has: { "userId": "P994E" } (already set, ignores any conflicting broadcast)
    /// </code>
    /// </remarks>
    private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
    {
        agentLogger.LogInformation(
            $"Agent '{AgentIntent}' received shared context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        aiContextProvider.MergeSharedContext(msg.UpdatedValues);
    }

    /// <summary>
    /// Executes the agent using AgentSession for automatic conversation history and context management.
    /// Handles conversation persistence, LLM interactions, interactive token detection, error handling, and response formatting.
    /// </summary>
    /// <param name="req">Agent request containing the user's message and optional classification</param>
    /// <returns>Task representing the async agent execution</returns>
    /// <remarks>
    /// <para><strong>Conversation Persistence:</strong></para>
    /// <para>Every turn is automatically persisted to an encrypted .morgana.json file named by conversationId.
    /// This enables resuming conversations across application restarts with full context and message history.</para>
    /// <code>
    /// Turn 1: User asks about billing
    /// → Load: No file exists, create new session
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

        try
        {
            // Load existing agent's conversation session from encrypted storage, or create new thread
            aiAgentSession ??= await persistenceService.LoadAgentConversationAsync(AgentIdentifier, this);
            if (aiAgentSession != null)
            {
                agentLogger.LogInformation($"Loaded existing conversation session for {AgentIdentifier}");
            }
            else
            {
                aiAgentSession = await aiAgent.GetNewSessionAsync();
                agentLogger.LogInformation($"Created new conversation session for {AgentIdentifier}");
            }

            // Execute agent on its conversation session (which has context and history)
            StringBuilder fullResponse = new StringBuilder();
            await foreach (AgentResponseUpdate chunk in aiAgent.RunStreamingAsync(
                new ChatMessage(ChatRole.User, req.Content!) { CreatedAt = DateTimeOffset.UtcNow }, aiAgentSession))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    fullResponse.Append(chunk.Text);

                    senderRef.Tell(new Records.AgentStreamChunk(chunk.Text));
                }
            }
            string llmResponseText = fullResponse.ToString();

            // Detect if LLM has emitted the special token for continuing the multi-turn conversation
            bool hasInteractiveToken = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);

            // Additional multi-turn heuristic: if agent is ending with a direct question with "?",
            // may it be a "polite" way of engaging the user in the continuation of this intent,
            // or an intentional question finalized to obtain further informations
            bool endsWithQuestion = llmResponseText.EndsWith('?');

            // Retrieve quick replies from tools (if any tools set them during execution)
            List<Records.QuickReply>? quickReplies = GetQuickRepliesFromContext();
            bool hasQuickReplies = quickReplies?.Count > 0;

            // Retrieve rich card from context (if LLM set one via SetRichCard tool)
            Records.RichCard? richCard = GetRichCardFromContext();
            bool hasRichCard = richCard != null;

            // Drop quick replies from context to prevent serialization (they're ephemeral UI hints)
            if (hasQuickReplies)
            {
                aiContextProvider.DropVariable("quick_replies");
                agentLogger.LogInformation($"Dropped {quickReplies!.Count} quick replies from context (ephemeral data)");
            }

            // Drop rich card from context to prevent serialization (it is ephemeral UI hint)
            if (hasRichCard)
            {
                aiContextProvider.DropVariable("rich_card");
                agentLogger.LogInformation($"Dropped rich card '{richCard!.Title}' from context (ephemeral data)");
            }

            // Request is completed when no further user engagement has been requested.
            // If agent offers QuickReplies, it MUST remain active to handle clicks
            // Otherwise, clicks would go through Classifier and risk "other" intent fallback
            bool isCompleted = !hasInteractiveToken && !endsWithQuestion && !hasQuickReplies;

            agentLogger.LogInformation(
                $"Agent response analysis: HasINT={hasInteractiveToken}," +
                $"EndsWithQuestion={endsWithQuestion}," +
                $"HasQR={hasQuickReplies}," +
                $"HasRC={hasRichCard}," +
                $"IsCompleted={isCompleted}");

            // Persist updated agent's conversation state
            await persistenceService.SaveAgentConversationAsync(AgentIdentifier, aiAgentSession, isCompleted);
            agentLogger.LogInformation($"Saved conversation state for {AgentIdentifier}");

            #if DEBUG
                senderRef.Tell(new Records.AgentResponse(llmResponseText, isCompleted, quickReplies, richCard));
            #else
                senderRef.Tell(new Records.AgentResponse(llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim(), isCompleted, quickReplies, richCard));
            #endif
        }
        catch (Exception ex)
        {
            agentLogger.LogError(ex, $"Error in {GetType().Name}");

            Self.Tell(new Records.FailureContext(new Status.Failure(ex), senderRef));
        }
    }

    /// <summary>
    /// Handles agent execution failures that occur during message processing.
    /// Sends a generic error response to the original sender to maintain conversation flow.
    /// </summary>
    /// <param name="failure">Failure context containing the exception and original sender reference</param>
    /// <remarks>
    /// <para>This handler is invoked when ExecuteAgentAsync encounters an unhandled exception.
    /// It ensures the conversation doesn't get stuck by returning a user-friendly error message
    /// and marking the interaction as completed (IsCompleted = true).</para>
    /// <para><strong>Error Recovery Strategy:</strong></para>
    /// <list type="bullet">
    /// <item>Log the exception for debugging</item>
    /// <item>Load generic error message from Morgana prompt configuration</item>
    /// <item>Send fallback response to original sender</item>
    /// <item>Mark interaction as completed to prevent stuck conversations</item>
    /// </list>
    /// </remarks>
    private async Task HandleAgentFailureAsync(Records.FailureContext failure)
    {
        agentLogger.LogError(failure.Failure.Cause, $"Agent execution failed in {GetType().Name}");

        Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync("Morgana");
        List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
        Records.ErrorAnswer? genericError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "GenericError", StringComparison.OrdinalIgnoreCase));

        failure.OriginalSender.Tell(new Records.AgentResponse(genericError?.Content ?? "An internal error occurred.", true, null));
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
    /// 1. LLM calls "GetInvoices" tool
    /// 2. Tool sets quick replies for guide selection
    /// 3. Tool returns guide list text
    /// 4. Agent calls GetQuickRepliesFromContext() after tool execution
    /// 5. Agent includes quick replies in AgentResponse
    /// 6. UI displays buttons to user
    /// </code>
    /// </remarks>
    protected List<Records.QuickReply>? GetQuickRepliesFromContext()
    {
        #region Utilities
        List<Records.QuickReply>? GetQuickReplies(string quickRepliesJSON)
        {
            try
            {
                // Deserialize JSON string to List<QuickReply>
                List<Records.QuickReply>? quickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(quickRepliesJSON);
                if (quickReplies != null && quickReplies.Any())
                {
                    agentLogger.LogInformation($"Retrieved {quickReplies.Count} quick replies from context");

                    return quickReplies;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize quick replies from context");

                // Clear corrupted data (prevent serialized context to be damaged)
                aiContextProvider.DropVariable("quick_replies");
            }

            return null;
        }
        #endregion

        // Retrieve quick_replies from context
        object? ctxQuickReplies = aiContextProvider.GetVariable("quick_replies");

        // We may find them in string format
        if (ctxQuickReplies is string ctxQuickRepliesJson && !string.IsNullOrEmpty(ctxQuickRepliesJson))
            return GetQuickReplies(ctxQuickRepliesJson);

        // Or we may find them in JsonElement format
        if (ctxQuickReplies is JsonElement ctxQuickRepliesJsonElement && ctxQuickRepliesJsonElement.ValueKind == JsonValueKind.String)
            return GetQuickReplies(ctxQuickRepliesJsonElement.GetString()!);

        return null;
    }

    /// <summary>
    /// Retrieves LLM-generated rich card from agent context after tool execution.
    /// Returns null if no rich card was set via SetRichCard tool.
    /// </summary>
    /// <returns>Deserialized RichCard object or null if not present/invalid</returns>
    /// <remarks>
    /// <para>This method is called after agent execution completes to extract any rich card
    /// that the LLM may have generated via the SetRichCard tool during its response generation.</para>
    /// <para>The rich card JSON is stored in the context under the reserved key "rich_card"
    /// and is dropped immediately after extraction (ephemeral data, not persisted).</para>
    /// </remarks>
    protected Records.RichCard? GetRichCardFromContext()
    {
        #region Utilities
        Records.RichCard? GetRichCard(string richCardJSON)
        {
            try
            {
                // Deserialize JSON string to RichCard
                Records.RichCard? richCard = JsonSerializer.Deserialize<Records.RichCard>(
                    richCardJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (richCard != null)
                {
                    agentLogger.LogInformation($"Retrieved rich card from context");

                    return richCard;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize rich card from context");

                // Clear corrupted data (prevent serialized context to be damaged)
                aiContextProvider.DropVariable("rich_card");
            }

            return null;
        }
        #endregion

        // Retrieve rich_card from context
        object? ctxRichCard = aiContextProvider.GetVariable("rich_card");

        // We may find it in string format
        if (ctxRichCard is string ctxRichCardJson && !string.IsNullOrEmpty(ctxRichCardJson))
            return GetRichCard(ctxRichCardJson);

        // Or we may find it in JsonElement format
        if (ctxRichCard is JsonElement ctxRichCardJsonElement && ctxRichCardJsonElement.ValueKind == JsonValueKind.String)
            return GetRichCard(ctxRichCardJsonElement.GetString()!);

        return null;
    }
}