using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using System.Reflection;
using System.Text.Json;

namespace Morgana.AI.Abstractions;

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
    protected AgentThread aiAgentThread;

    /// <summary>
    /// Context provider for reading and writing conversation variables.
    /// Manages both local (agent-specific) and shared (cross-agent) context.
    /// Also used for quick reply storage/retrieval via special key "__pending_quick_replies".
    /// </summary>
    protected MorganaContextProvider contextProvider;

    /// <summary>
    /// Logger instance for agent-level logging (separate from actorLogger).
    /// Uses Microsoft.Extensions.Logging for consistency with agent framework.
    /// </summary>
    protected readonly ILogger agentLogger;

    /// <summary>
    /// Initializes a new instance of MorganaAgent with AI agent infrastructure.
    /// Sets up conversation threading, context management, and inter-agent communication.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation this agent will handle</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="agentLogger">Logger instance for agent-level diagnostics</param>
    public MorganaAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger agentLogger) : base(conversationId, llmService, promptResolverService)
    {
        this.agentLogger = agentLogger;

        Receive<Records.ReceiveContextUpdate>(HandleContextUpdate);
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
        string intent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "unknown";

        agentLogger.LogInformation($"Agent {intent} broadcasting shared context variable: {key}");

        Context.ActorSelection($"/user/router-{conversationId}")
            .Tell(new Records.BroadcastContextUpdate(
                intent,
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
        string myIntent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? "unknown";

        agentLogger.LogInformation(
            $"Agent '{myIntent}' received shared context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        contextProvider.MergeSharedContext(msg.UpdatedValues);
    }

    /// <summary>
    /// Executes the agent using AgentThread for automatic conversation history and context management.
    /// Handles LLM interactions, interactive token detection, error handling, and response formatting.
    /// </summary>
    /// <param name="req">Agent request containing the user's message and optional classification</param>
    /// <returns>Task representing the async agent execution</returns>
    /// <remarks>
    /// <para><strong>Execution Flow:</strong></para>
    /// <list type="number">
    /// <item>Create or reuse AgentThread for conversation history continuity</item>
    /// <item>Send user message to LLM via aiAgent.RunAsync</item>
    /// <item>Detect #INT# token in response (signals incomplete interaction)</item>
    /// <item>Remove #INT# token from production responses (kept in debug for testing)</item>
    /// <item>Send AgentResponse with completion flag to supervisor</item>
    /// </list>
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
            aiAgentThread ??= aiAgent.GetNewThread();

            AgentRunResponse llmResponse = await aiAgent.RunAsync(req.Content!, aiAgentThread);
            string llmResponseText = llmResponse.Text;

            // Detect if LLM has emitted the special token for continuing the multi-turn conversation
            bool hasInteractiveToken = llmResponseText.Contains("#INT#", StringComparison.OrdinalIgnoreCase);

            // Additional heuristic - if current agent is ending with a direct question with "?",
            // may it be a "polite" way of engaging the user in the continuation of this intent,
            // or an intentional question finalized to obtain further informations
            bool endsWithQuestion = llmResponseText.EndsWith("?");

            // Retrieve quick replies from tools (if any tools set them during execution)
            List<Records.QuickReply>? quickReplies = RetrieveToolQuickReplies();
            bool hasQuickReplies = quickReplies != null && quickReplies.Any();

            // Request is completed when no further user engagement has been requested.
            // If agent offers QuickReplies, it MUST remain active to handle clicks
            // Otherwise, clicks would go through Classifier and risk "other" intent fallback
            bool isCompleted = !hasInteractiveToken && !endsWithQuestion && !hasQuickReplies;

            agentLogger.LogInformation(
                $"Agent response analysis: HasINT={hasInteractiveToken}, EndsWithQuestion={endsWithQuestion}, HasQR={hasQuickReplies}, IsCompleted={isCompleted}");

            #if DEBUG
                string cleanText = llmResponseText;
            #else
                string cleanText = llmResponseText.Replace("#INT#", "", StringComparison.OrdinalIgnoreCase).Trim();
            #endif

            senderRef.Tell(new Records.AgentResponse(cleanText, isCompleted, quickReplies));
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
    /// 4. Agent calls RetrieveToolQuickReplies() after tool execution
    /// 5. Agent includes quick replies in AgentResponse
    /// 6. UI displays buttons to user
    /// </code>
    /// <para><strong>Implementation Note:</strong></para>
    /// <para>Retrieves quick replies from the shared ContextProvider using the special key "__pending_quick_replies".
    /// MorganaTool.SetQuickReplies() stores them as JSON string, so we deserialize here.
    /// The ContextProvider stores all values as JSON strings, not typed objects.</para>
    /// </remarks>
    protected List<Records.QuickReply>? RetrieveToolQuickReplies()
    {
        // Retrieve from context (ContextProvider stores as JSON string)
        string? quickRepliesJson = contextProvider.GetVariable("__pending_quick_replies") as string;
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
                    contextProvider.DropVariable("__pending_quick_replies");

                    return quickReplies;
                }
            }
            catch (JsonException ex)
            {
                agentLogger.LogError(ex, "Failed to deserialize quick replies from context");

                // Clear corrupted data
                contextProvider.DropVariable("__pending_quick_replies");
            }
        }
        
        return null;
    }
}