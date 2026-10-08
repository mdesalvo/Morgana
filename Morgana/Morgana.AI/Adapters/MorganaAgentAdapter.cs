using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using Morgana.AI.Tools;
using Morgana.AI.Workflows;

namespace Morgana.AI.Adapters;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Creates and configures AIAgent instances from Morgana agent definitions.
/// Handles instruction composition, tool/MCP registration, provider setup.
/// Uses session accessor pattern: MorganaAIContextProvider + Func&lt;ToolContext&gt; factory.
/// </summary>
public class MorganaAgentAdapter
{
    /// <summary>
    /// Service for resolving prompt templates from configuration sources (morgana.json, agents.json).
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// LLM service abstraction, queried per-agent for the chat client and dust pricing of the
    /// tier its <c>[RequiresLLMTier]</c> attribute declares. There is no single process-wide
    /// chat client here anymore — each agent resolves its own tier at creation time.
    /// </summary>
    protected readonly ILLMService llmService;

    /// <summary>
    /// Service for discovering custom MorganaTool implementations via [ProvidesToolForIntent] attribute.
    /// Returns null if no custom tool exists for an intent (MCP-only agents).
    /// </summary>
    protected readonly IToolRegistryService toolRegistryService;

    /// <summary>
    /// Service for managing MCP (Model Context Protocol) client connections and lifecycle.
    /// Provides connection pooling and tool discovery from external MCP servers.
    /// </summary>
    protected readonly IMCPClientRegistryService imcpClientRegistryService;

    /// <summary>
    /// Service for creating IChatReducer instances for context window management.
    /// Creates SummarizingChatReducer based on configuration to optimize LLM costs.
    /// </summary>
    protected readonly HistoryReducerService chatReducerService;

    /// <summary>
    /// Per-conversation lifetime token-budget limiter. Domain-agent LLM calls (and their
    /// history reducer) are metered through it under a per-agent role.
    /// </summary>
    protected readonly IDustLimitService dustLimitService;

    /// <summary>
    /// Describes the agents of the ecosystem to one another. Consulted once per
    /// <c>[ConsultsAgent]</c> declaration, to obtain the colleague's card.
    /// </summary>
    protected readonly IAgentDirectoryService agentDirectoryService;

    /// <summary>
    /// Application configuration, read for the peer-consultation budget and timeout.
    /// </summary>
    protected readonly IConfiguration configuration;

    /// <summary>
    /// Logger instance for agent creation diagnostics and tool registration tracking.
    /// </summary>
    protected readonly ILogger logger;

    /// <summary>
    /// Morgana framework prompt containing global policies and error message templates.
    /// Loaded once during adapter initialization from morgana.json.
    /// </summary>
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// The framework's base tool (Reply), projected from <see cref="ReplyTool"/> and stamped <c>Reserved = true</c> exactly
    /// once here — the only place in the codebase that ever sets it true. Every other reader of a
    /// ToolDefinition's Reserved flag (domain tools included) sees false by construction, never by
    /// a check: see the Reserved remarks on Records.ToolDefinition.
    /// </summary>
    protected readonly Records.ToolDefinition[] morganaTools;

    /// <summary>
    /// Assembles the framework prose the agent's model reads: the composed system prompt, the
    /// colleagues declaration and each colleague's description.
    /// </summary>
    protected readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Initializes a new instance of the MorganaAgentAdapter.
    /// Loads the Morgana framework prompt for later composition with domain prompts.
    /// </summary>
    /// <param name="llmService">LLM service abstraction, queried per-agent for its declared tier's chat client and pricing</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="promptComposerService">Service composing the framework prose the model reads</param>
    /// <param name="toolRegistryService">Service for discovering custom MorganaTool implementations</param>
    /// <param name="imcpClientRegistryService">Service for managing MCP server connections</param>
    /// <param name="chatReducerService">Service for reducing context window sent to LLM</param>
    /// <param name="dustLimitService">Per-conversation lifetime token-budget limiter</param>
    /// <param name="agentDirectoryService">Supplies the card of each declared colleague and resolves it into a callable agent</param>
    /// <param name="configuration">Application configuration, read for the peer-consultation budget and timeout</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaAgentAdapter(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IPromptComposerService promptComposerService,
        IToolRegistryService toolRegistryService,
        IMCPClientRegistryService imcpClientRegistryService,
        HistoryReducerService chatReducerService,
        IDustLimitService dustLimitService,
        IAgentDirectoryService agentDirectoryService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.promptComposerService = promptComposerService;
        this.toolRegistryService = toolRegistryService;
        this.imcpClientRegistryService = imcpClientRegistryService;
        this.chatReducerService = chatReducerService;
        this.dustLimitService = dustLimitService;
        this.agentDirectoryService = agentDirectoryService;
        this.configuration = configuration;
        this.logger = logger;

        morganaPrompt = promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();

        morganaTools = [.. ProvidesToolForIntentRegistryService.ProjectToolDefinitions(typeof(ReplyTool))
            .Select(t => t with { Reserved = true })];
    }

    /// <summary>
    /// Creates a fully configured <see cref="AIAgent"/> instance for the given agent type.
    /// </summary>
    /// <param name="agentType">
    /// Agent class decorated with <c>[HandlesIntent]</c>.
    /// </param>
    /// <param name="conversationId">
    /// Identifier of the ongoing conversation.
    /// </param>
    /// <param name="sessionAccessor">
    /// Returns the agent's current <see cref="AgentSession"/> at tool-call time.
    /// Wire as <c>() =&gt; CurrentSession</c> from the concrete <see cref="MorganaAgent"/> subclass.
    /// May return <c>null</c> at construction time; guaranteed non-null during actual tool execution.
    /// </param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when the agent writes a shared context variable. Wire to
    /// <see cref="MorganaAgent.OnSharedContextUpdate"/>, which persists the value into the
    /// conversation-scoped <c>shared_context</c> registry so other agents pick it up at the
    /// start of their next turn.
    /// </param>
    /// <returns>
    /// A tuple of (AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider) —
    /// all three singletons for this agent instance.
    /// </returns>
    public (AIAgent agent, MorganaAIContextProvider contextProvider, MorganaChatHistoryProvider historyProvider) CreateAgent(
        Type agentType,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        Func<string, object, Task>? sharedContextCallback = null)
        // The single sync-over-async point of the whole creation path and it is a structural
        // boundary rather than a shortcut: a MorganaAgent is materialized by Akka through
        // DependencyResolver.Props, i.e. inside a constructor, which offers no async seam. Everything
        // below this line is properly awaited; callers that DO have one — Forge composing a draft
        // agent, or a future async actor-initialization pattern — should call CreateAgentAsync
        // directly and never come through here.
        => CreateAgentAsync(agentType, conversationId, sessionAccessor, sharedContextCallback)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Asynchronous counterpart of <see cref="CreateAgent"/> and the real implementation: prompt
    /// resolution, prompt composition and tool-description assembly are all awaited here.
    /// </summary>
    /// <inheritdoc cref="CreateAgent" path="/param"/>
    /// <returns>
    /// A tuple of (AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider) —
    /// all three singletons for this agent instance.
    /// </returns>
    public async Task<(AIAgent agent, MorganaAIContextProvider contextProvider, MorganaChatHistoryProvider historyProvider)> CreateAgentAsync(
        Type agentType,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        Func<string, object, Task>? sharedContextCallback = null)
    {
        // 1) Identity: the [HandlesIntent] attribute is the agent's contract. Its absence
        //    is a wiring bug (a MorganaAgent subclass that forgot the attribute), so fail
        //    loud at creation rather than silently producing an unroutable agent.
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        // 1b) Tier: the agent's fixed, "existential" declaration of which model class it runs
        //     on. Mandatory alongside [HandlesIntent] — see RequiresLLMTierAttribute remarks.
        //     Startup validation (HandlesIntentAgentRegistryService) already guarantees this
        //     attribute is present and its tier is configured for the active provider before
        //     any agent is ever created, so both lookups below are safe.
        RequiresLLMTierAttribute tierAttribute = agentType.GetCustomAttribute<RequiresLLMTierAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [RequiresLLMTier] attribute");

        logger.LogInformation("Creating agent for intent '{IntentAttributeIntent}' on tier '{Tier}'...", intentAttribute.Intent, tierAttribute.Tier);

        // 2) Domain prompt for this intent (instructions/personality/formatting),
        //    resolved from agents.json.
        Records.Prompt agentPrompt = await promptResolverService.ResolveAsync(intentAttribute.Intent);

        // 3) Tool surface = framework base tool (Reply) UNION the agent's
        //    domain tools (projected from its tool class). Union de-dups so a domain tool can't shadow a base one.
        Records.ToolDefinition[] domainTools = [.. toolRegistryService.GetToolDefinitions(intentAttribute.Intent)];
        Records.ToolDefinition[] agentTools = [.. morganaTools.Union(domainTools)];

        // 3b) Collect the tools of every [UsesMCPServer] on the agent. Best-effort by design: a server
        //     that is down or misconfigured is logged per-server and skipped, never aborting agent
        //     creation. They come before the workflows because a workflow may cite them. They stay
        //     apart from the native adapter because each one arrives already an AIFunction.
        List<AIFunction> mcpTools = await RegisterMCPToolsAsync(
            agentType,
            [.. agentTools.Select(tool => tool.Name), .. toolRegistryService.GetWorkflowDefinitions(intentAttribute.Intent).Select(workflow => Constants.Workflows.LauncherPrefix + workflow.Name)]);
        Records.ToolDefinition[] mcpToolDefinitions = [.. mcpTools.Select(ProjectMCPTool)];

        // 3c) The workflows the agent keeps. Startup could not weigh a tool that arrives from a server, so
        //     an agent with MCP tools has its workflows weighed here against the tools it really holds. A
        //     workflow that does not hold is withdrawn from this agent and the agent lives on.
        IReadOnlyList<Records.WorkflowDefinition> workflowDefinitions = agentType.GetCustomAttributes<UsesMCPServerAttribute>().Any()
            ? KeepSoundWorkflows(intentAttribute.Intent, agentType, toolRegistryService.GetWorkflowDefinitions(intentAttribute.Intent), [.. domainTools, .. mcpToolDefinitions])
            : toolRegistryService.GetWorkflowDefinitions(intentAttribute.Intent);

        // 4) Per-agent context provider (the variable store that the context-scoped parameters are
        //    resolved from); sharedContextCallback wires Shared:true writes into the cross-agent registry.
        MorganaAIContextProvider morganaAIContextProvider = CreateAIContextProvider(
            intentAttribute.Intent,
            agentTools,
            workflowDefinitions,
            sharedContextCallback);

        // 5) ToolContext factory — evaluated lazily on EACH tool call, never now. The
        //    adapter holds no actor reference, so the session is pulled fresh via
        //    sessionAccessor at call time (Akka's single-thread guarantee makes it
        //    non-null during execution). A null here means the agent was invoked without
        //    ExecuteAgentAsync seeding the session — a hard wiring error, so throw.
        // Filled once every tool is known below and read by Reply at every call: the tools that a user
        // action may lead to, so an action naming anything else is discarded.
        List<string> actionableToolNames = [];

        // The agent's workflows, whose engine is built here and whose position lives in the session. An agent
        // declaring none carries no workflow machinery at all.
        AgentWorkflows? agentWorkflows = workflowDefinitions.Count == 0
            ? null
            : new AgentWorkflows(
                new WorkflowEngine(workflowDefinitions),
                workflowDefinitions,
                domainTools.Concat(mcpToolDefinitions).GroupBy(tool => tool.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
                mcpTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal),
                morganaAIContextProvider,
                sessionAccessor);

        Func<MorganaTool.ToolContext> toolContextFactory = () =>
        {
            AgentSession session = sessionAccessor()
                ?? throw new InvalidOperationException(
                    $"Agent '{intentAttribute.Intent}' has no active session during tool execution. " +
                    $"Ensure ExecuteAgentAsync sets aiAgentSession before invoking the agent.");

            // Inside a workflow a button may only lead to a tool of the step the workflow stands at.
            IReadOnlyCollection<string> actionable = agentWorkflows is not null
                && morganaAIContextProvider.GetWorkflowPosition(session)?.Resolve(workflowDefinitions) is { } running
                    ? running.Step.Tools
                    : actionableToolNames;

            // A step naming several tools is a choice: Reply holds the turn's buttons to exactly those tools.
            (string Workflow, string Step, IReadOnlyList<string> Tools)? choiceStep = agentWorkflows is not null
                && morganaAIContextProvider.GetWorkflowPosition(session)?.Resolve(workflowDefinitions) is { Step.Tools.Count: > 1 } choosing
                    ? (choosing.Definition.Name, choosing.Step.Name, choosing.Step.Tools)
                    : null;

            return new MorganaTool.ToolContext(morganaAIContextProvider, session, conversationId, actionable, choiceStep);
        };

        // 6a) Bind the tool definitions to their delegates (native MorganaTool methods), then
        //    layer on any [UsesMCPServer] tools discovered from external MCP servers.
        MorganaToolAdapter morganaToolAdapter = CreateToolAdapterForIntent(
            intentAttribute.Intent,
            agentTools,
            toolContextFactory);

        // An action leads to something this agent does itself: its domain tools and its MCP tools.
        // Reply closes turns and a colleague is consulted, never pressed for, so neither is listed.
        actionableToolNames.AddRange(agentTools.Where(tool => !tool.Reserved).Select(tool => tool.Name));
        actionableToolNames.AddRange(mcpTools.Select(tool => tool.Name));

        // 6c) Collect the colleagues this agent declares it may consult. Like MCP tools they arrive
        //     already AIFunctions and bypass the native adapter entirely — they are not declared in
        //     any tool class, are not implemented by any MorganaTool and their prose is the colleague's
        //     own card rather than something this agent's author wrote.
        Dictionary<string, string> peerTerritories = [];
        List<AIFunction> peerAgents = await RegisterPeerAgentsAsync(
            agentType,
            intentAttribute.Intent,
            conversationId,
            sessionAccessor,
            morganaAIContextProvider,
            peerTerritories);

        // 7) Resolve THIS agent's own tier client/pricing (never the framework-default
        //    client) and wrap it in a per-agent dust meter. The role label
        //    ("Morgana (Billing/Efficiency)" etc.) attributes consumption to this agent+tier in
        //    the budget; conversationId scopes the charge. The reducer is built on the SAME
        //    wrapped client so its summarization LLM calls (also token-bearing) are
        //    metered too, not silently free.
        string intent = intentAttribute.Intent;
        // Builds a human-readable label for the dust ledger and OTel tags, e.g. "billing" ->
        // "Morgana (Billing/Efficiency)". Qualifies the same framework role the pipeline charges
        // under, so a ledger grouped by prefix keeps every charge of one installation together.
        string dustRole = $"{Constants.Morgana} ({char.ToUpperInvariant(intent[0])}{intent[1..]}/{tierAttribute.Tier})";
        IChatClient tierChatClient = llmService.GetChatClient(tierAttribute.Tier);
        Records.MagicDustPricing tierPricing = llmService.GetPricing(tierAttribute.Tier);
        IChatClient agentChatClient =
            new DustAccountingChatClient(tierChatClient, dustLimitService, tierPricing, dustRole, conversationId);

        // 8) History provider: keeps the full transcript in AgentSession, exposes the
        //    (optionally reduced) view to the LLM. Null reducer → full history verbatim.
        IChatReducer? chatReducer = chatReducerService.CreateReducer(agentChatClient);
        MorganaChatHistoryProvider chatHistoryProvider = new MorganaChatHistoryProvider(
            intentAttribute.Intent, chatReducer, logger, promptComposerService: promptComposerService);

        // 9) Assemble the Microsoft.Agents.AI agent over the metered client, injecting the
        //    context + history providers, a stable per-conversation Id (intent-conversationId),
        //    the two-layer composed instructions (framework prompt + domain prompt) and the
        //    tool delegates materialized as AIFunctions.
        // The tool loop the agent runs on. Reply's argument errors go back to the model in full, so a
        // card breaking its schema is repaired on the next call rather than lost; every other tool
        // fails as tersely as before, keeping a domain tool's internals out of the model's sight.
        // Below the loop, a response asking for the user's approval loses its Reply: that turn is the
        // framework's to close, with the approval buttons.
        // Between the loop and the approval client, the tools that the workflow state allows are what the model is offered.
        FunctionInvokingChatClient toolLoopChatClient = new FunctionInvokingChatClient(
            new WorkflowToolsChatClient(new ApprovalTurnChatClient(agentChatClient), sessionAccessor, morganaAIContextProvider, workflowDefinitions))
        {
            FunctionInvoker = (context, cancellationToken) => InvokeToolAsync(context, agentWorkflows, cancellationToken)
        };

        // Above the loop, where a whole turn is visible: a turn that the model wrote without Reply is
        // closed here, on whichever path the provider supports.
        TurnClosingChatClient turnClosingChatClient = new TurnClosingChatClient(
            toolLoopChatClient,
            await promptComposerService.ComposeTurnClosureRequestAsync(),
            await promptComposerService.ComposeToolResultAsync(Constants.ToolResults.TurnClosed),
            llmService.CanForceToolCall,
            logger);

        // A launcher exists only for a workflow that the agent keeps.
        List<AIFunction> launcherFunctions = agentWorkflows is null
            ? []
            : [.. workflowDefinitions.Select(workflow => (AIFunction)new WorkflowLauncherFunction(
                Constants.Workflows.LauncherPrefix + workflow.Name,
                workflow.Description,
                () => LaunchWorkflowAsync(workflow, agentWorkflows)))];

        AIAgent aiAgent = turnClosingChatClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                // Give the agent its context providers
                AIContextProviders = [morganaAIContextProvider],

                // Give the agent its history provider
                ChatHistoryProvider = chatHistoryProvider,

                // Give the agent its identifiers
                Id = $"{intentAttribute.Intent.ToLowerInvariant()}-{conversationId}",
                Name = intentAttribute.Intent,

                // Give the agent its instructions and tools
                ChatOptions = new ChatOptions
                {
                    // Instructions of the agent may add A2A peer consultation directives
                    Instructions = await ComposeInstructionsWithColleaguesAsync(
                        agentPrompt,

                        // Both ends of the topology, not just the asking one: an agent that consults
                        // nobody still reads the peer-consultation rules, because with the ring raised
                        // whole a colleague's question can land on it at any turn.
                        PeerConsultationEnabled,
                        peerTerritories),
                    Tools = [.. await morganaToolAdapter.CreateAllFunctionsAsync(), .. mcpTools, .. peerAgents, .. launcherFunctions]
                }
            });

        // 10) Return all three: the caller (MorganaAgent subclass) keeps the provider and
        //     history-provider handles to drive context/history across turns — the agent
        //     alone is not enough because providers are queried/mutated outside InvokeAsync.
        return (aiAgent, morganaAIContextProvider, chatHistoryProvider);
    }

    /// <summary>
    /// Runs one tool call of the agent's loop, turning a refused Reply — arguments breaking their
    /// schema, a card too large, a turn not yet written — into the framework's answer for that event.
    /// Inside a workflow it also holds the call to the current step, binds what the step binds and
    /// feeds the result to the engine.
    /// </summary>
    /// <param name="context">The call that the tool loop is about to run.</param>
    /// <param name="workflows">The agent's workflow machinery; <c>null</c> for an agent declaring none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async ValueTask<object?> InvokeToolAsync(FunctionInvocationContext context, AgentWorkflows? workflows, CancellationToken cancellationToken)
    {
        bool isReply = string.Equals(context.Function.Name, Constants.Tools.Reply, StringComparison.Ordinal);

        // Before anything else: a tool that the workflow hides is not run, whatever the model was shown.
        AgentSession? session = workflows is null || isReply ? null : workflows.SessionAccessor();
        Records.WorkflowPosition? position = session is null ? null : workflows!.ContextProvider.GetWorkflowPosition(session);
        (Records.WorkflowDefinition Definition, Records.WorkflowStep Step)? running = position?.Resolve(workflows!.Definitions);
        bool isCurrentStepTool = false;

        if (running is { } active)
        {
            string toolName = context.Function.Name;
            HashSet<string> signature = active.Definition.ToolSignature();
            isCurrentStepTool = active.Step.Tools.Contains(toolName);

            // A colleague is consulted at any step: it is a private method of the workflow, never part of it.
            bool isHidden = !toolName.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal)
                && !isCurrentStepTool
                && (context.Function is WorkflowLauncherFunction
                    || signature.Contains(toolName)
                    || (workflows!.Tools.TryGetValue(toolName, out Records.ToolDefinition? declared) && declared.RequiresExecutionApproval));

            if (isHidden)
            {
                logger.LogWarning("Agent called '{Tool}', which workflow '{Workflow}' does not offer at its step '{Step}'", toolName, active.Definition.Name, active.Step.Name);
                return await promptComposerService.ComposeToolResultAsync(
                    Constants.ToolResults.ToolNotAtThisStep,
                    ToolNotAtThisStepValues(toolName, active.Definition.Name));
            }

            if (isCurrentStepTool)
                BindStepArguments(context, position!);
        }

        object? result;
        try
        {
            result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && isReply)
        {
            // Arguments breaking their schema: the reason is the deserializer's own account of what is wrong.
            logger.LogWarning("Reply refused: {Reason}", ex.Message);
            return await promptComposerService.ComposeReplyNotAcceptedAsync(ex.Message);
        }

        // Anything but a framework tool's named result reaches the model as the tool returned it, a
        // current-step tool's one under the label of where the workflow stands now.
        if (result is not Records.FrameworkToolResult named)
            return isCurrentStepTool
                ? await AdvanceWorkflowAsync(context.Function.Name, workflows!, session!, position!, running!.Value.Definition.Name, result)
                : result;

        string text = await promptComposerService.ComposeToolResultAsync(named.Name, named.Values);
        if (!isReply || named.Name == Constants.ToolResults.TurnClosed)
            return text;

        // The turn stays open: the model reads why and closes again without narrating it to the user.
        logger.LogWarning("Reply refused: {Reason}", text);
        return await promptComposerService.ComposeReplyNotAcceptedAsync(text);
    }

    /// <summary>
    /// Writes the parameters that the current step binds into the call, over whatever the model passed for them.
    /// </summary>
    private static void BindStepArguments(FunctionInvocationContext context, Records.WorkflowPosition position)
    {
        // Only a parameter the tool has: a step binds for all its tools, which do not all take every one.
        JsonElement schema = context.Function.JsonSchema;
        if (!schema.TryGetProperty("properties", out JsonElement properties))
            return;

        foreach ((string carriedName, string valueJson) in position.Arguments)
        {
            // A workflow carries its property's name while a tool spells its parameter its own way,
            // so the match ignores case and the value is written under the schema's spelling.
            foreach (JsonProperty schemaProperty in properties.EnumerateObject())
            {
                if (string.Equals(schemaProperty.Name, carriedName, StringComparison.OrdinalIgnoreCase))
                    context.Arguments[schemaProperty.Name] = JsonDocument.Parse(valueJson).RootElement.Clone();
            }
        }
    }

    /// <summary>
    /// Hands the result of a current-step tool to the engine and stores where the workflow stands afterwards.
    /// </summary>
    /// <returns>The result as the model reads it: under the label of the step reached or of the end.</returns>
    private async Task<object?> AdvanceWorkflowAsync(
        string toolName,
        AgentWorkflows workflows,
        AgentSession session,
        Records.WorkflowPosition position,
        string workflowName,
        object? result)
    {
        string resultText = result switch
        {
            string text => text,
            JsonElement element => element.GetRawText(),
            // A plain-text MCP result arrives as a content block: the model reads its text, not the block's serialization.
            TextContent content => content.Text,
            _ => JsonSerializer.Serialize(result)
        };

        // Where the failure marker and the fields sit depends on the tool's origin, never on the shape of its result.
        Records.StepOutcome outcome = WorkflowEngine.ReadOutcome(toolName, resultText, workflows.MCPToolNames.Contains(toolName));

        Records.WorkflowPosition? next;
        try
        {
            next = await workflows.Engine.AdvanceAsync(position, outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The tool has already done its work: a position that the engine cannot advance would pin the
            // agent to one step for good, so the workflow is dropped and the result stands as it is.
            logger.LogError(ex, "Workflow '{Workflow}' could not advance from step '{Step}' after '{Tool}': it is dropped", workflowName, position.Step, toolName);
            workflows.ContextProvider.DropWorkflowPosition(session);
            return result;
        }

        if (next is null)
            workflows.ContextProvider.DropWorkflowPosition(session);
        else
            workflows.ContextProvider.SetWorkflowPosition(session, next);

        return await promptComposerService.ComposeWorkflowResultAsync(workflowName, next?.Step, resultText) ?? result;
    }

    /// <summary>The values of <see cref="Constants.ToolResults.ToolNotAtThisStep"/>.</summary>
    private static Dictionary<string, string> ToolNotAtThisStepValues(string toolName, string workflowName)
        => new()
        {
            [Constants.Placeholders.ToolName] = toolName,
            [Constants.Placeholders.Workflow] = workflowName
        };

    /// <summary>
    /// Starts one workflow of the agent and stores where it stands, unless the call is not one that may open a workflow.
    /// </summary>
    /// <param name="definition">The workflow that the called launcher starts.</param>
    /// <param name="workflows">The agent's workflow machinery.</param>
    private async ValueTask<object?> LaunchWorkflowAsync(Records.WorkflowDefinition definition, AgentWorkflows workflows)
    {
        string launcherName = Constants.Workflows.LauncherPrefix + definition.Name;
        AgentSession session = workflows.SessionAccessor()
            ?? throw new InvalidOperationException($"{launcherName} was called with no active session");

        Records.WorkflowPosition? running = workflows.ContextProvider.GetWorkflowPosition(session);
        bool servingConsultation = workflows.ContextProvider.GetVariable(session, Constants.ContextKeys.ServingConsultation) is not null;

        // One workflow at a time and never for a colleague: the call changes nothing and is told so.
        // A stored position that no declaration serves any more does not count as a workflow running.
        bool isRunning = running?.Resolve(workflows.Definitions) is not null;
        if (isRunning || servingConsultation)
            return new Records.FrameworkToolResult(
                Constants.ToolResults.ToolNotAtThisStep,
                ToolNotAtThisStepValues(launcherName, isRunning ? running!.Workflow : definition.Name));

        Records.WorkflowPosition position = await workflows.Engine.LaunchAsync(definition.Name);
        workflows.ContextProvider.SetWorkflowPosition(session, position);

        return new Records.FrameworkToolResult(Constants.ToolResults.WorkflowStarted, new Dictionary<string, string>
        {
            [Constants.Placeholders.Workflow] = definition.Name,
            [Constants.Placeholders.Step] = position.Step
        });
    }

    /// <summary>
    /// Creates and configures a MorganaAIContextProvider for an agent with shared variable detection.
    /// Analyzes tool definitions to identify variables that participate in the conversation-scoped
    /// shared_context registry.
    /// </summary>
    /// <param name="agentName">Name of the agent for logging purposes (e.g., "billing")</param>
    /// <param name="tools">Tool definitions to scan for shared variable declarations</param>
    /// <param name="workflows">The agent's workflows, which the provider hands to whoever resolves a stored position.</param>
    /// <param name="sharedContextCallback">
    /// Optional callback invoked when a shared variable is set. Wired to agent's
    /// OnSharedContextUpdate which persists the value via IConversationPersistenceService.
    /// </param>
    /// <returns>Configured MorganaAIContextProvider instance for the agent</returns>
    private MorganaAIContextProvider CreateAIContextProvider(
        string agentName,
        IEnumerable<Records.ToolDefinition> tools,
        IReadOnlyList<Records.WorkflowDefinition> workflows,
        Func<string, object, Task>? sharedContextCallback = null)
    {
        // Derive the shared-variable allow-list from the tool definitions: a parameter is
        // cross-agent shared only if it is BOTH flagged Shared AND context-scoped. The
        // Scope=="context" guard is essential — a Shared but request-scoped parameter is
        // asked of the user every turn, not carried in the registry, so promoting it would
        // wrongly route a per-turn input into first-write-wins shared state. Flatten across
        // all tools and Distinct() because the same logical variable (e.g. "customerCode") is
        // typically declared on several tools and must register exactly once.
        List<string> sharedVariables = [.. tools
             .SelectMany(t => t.Parameters)
             .Where(p => p.Shared && string.Equals(p.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
             .Select(p => p.Name)
             .Distinct()];

        // Startup-visible diagnostic: the shared set is part of the cross-agent contract,
        // so surface it (or its emptiness) explicitly rather than leaving it implicit.
        logger.LogInformation(
            sharedVariables.Count > 0
                ? $"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}"
                : $"Agent '{agentName}' has NO shared variables");

        // The provider needs the allow-list up front: only writes to a name in this set
        // trigger OnSharedContextUpdate; everything else stays agent-local.
        MorganaAIContextProvider aiContextProvider = new MorganaAIContextProvider(logger, sharedVariables, workflows: workflows);

        // Wire persistence only when a callback was supplied. Left null (e.g. an agent
        // created outside the actor path) shared writes still update local state but are
        // not propagated to the conversation-scoped registry — no NPE, just no fan-out.
        if (sharedContextCallback != null)
            aiContextProvider.OnSharedContextUpdate = sharedContextCallback;

        return aiContextProvider;
    }

    /// <summary>
    /// Creates a <see cref="MorganaToolAdapter"/> with base tools always registered
    /// and optional intent-specific custom tools registered when a matching
    /// <see cref="MorganaTool"/> subclass is found in the tool registry.
    /// </summary>
    /// <param name="intent">Agent intent name.</param>
    /// <param name="agentTools">Merged tool definitions of the framework base tool and the intent's tool class.</param>
    /// <param name="toolContextFactory">Factory supplying the (provider, session) pair to tool constructors.</param>
    /// <returns>Configured MorganaToolAdapter with registered tool implementations</returns>
    private MorganaToolAdapter CreateToolAdapterForIntent(
        string intent,
        Records.ToolDefinition[] agentTools,
        Func<MorganaTool.ToolContext> toolContextFactory)
    {
        // The adapter resolves every context-scoped parameter from the session the factory hands it
        // at invocation, so the model never looks a value up nor stores one itself.
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(logger, toolContextFactory, promptComposerService);

        // Split the merged set back into base (ReplyTool, the `morganaTools` field) vs
        // intent-specific (the tool class). Compare by Name only: the incoming `agentTools` array
        // was produced by a Union that may carry distinct ToolDefinition instances for the same
        // logical tool, so reference/value equality would wrongly classify a base tool as
        // intent-specific. Name is the stable identity (tool method names are unique).
        Records.ToolDefinition[] agentSpecificTools = [.. agentTools.Except(morganaTools, new ToolDefinitionNameComparer())];

        // ALWAYS register the base tool (Reply). It is implemented by ReplyTool, so every agent
        // closes its turns the same way, even an MCP-only or tool-less one.
        MorganaTool baseTool = new ReplyTool(logger, toolContextFactory);
        RegisterToolsInAdapter(morganaToolAdapter, baseTool, morganaTools);
        logger.LogInformation("Registered {BaseToolsLength} base tools for intent '{Intent}'", morganaTools.Length, intent);

        // Base-tools-only agent: nothing domain-specific declared → done.
        if (agentSpecificTools.Length == 0)
        {
            logger.LogInformation("No intent-specific tools defined for intent '{Intent}' (agent has base tools only)", intent);
            return morganaToolAdapter;
        }

        // Domain tools are projected from their class, so one exists only where the registry holds that class.
        Type toolType = toolRegistryService.FindToolTypeForIntent(intent)!;

        logger.LogInformation("Found custom native tool: {ToolTypeName} for intent '{Intent}' via ToolRegistry", toolType.Name, intent);

        // A discovered tool class that cannot be instantiated is a hard authoring bug, almost always
        // a constructor that does not match the required (ILogger, Func<MorganaTool.ToolContext>)
        // signature. Fail loud with that exact remediation rather than silently shipping an agent
        // missing its domain tools.
        MorganaTool customToolInstance;
        try
        {
            customToolInstance = (MorganaTool)Activator.CreateInstance(toolType, logger, toolContextFactory)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to instantiate custom tool {ToolTypeName} for intent '{Intent}'", toolType.Name, intent);
            throw new InvalidOperationException(
                $"Could not create custom tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor accepting " +
                $"(ILogger, Func<MorganaTool.ToolContext>).", ex);
        }

        // Bind only the intent-specific definitions to the discovered instance (base tools
        // were already registered above against the base instance).
        RegisterToolsInAdapter(morganaToolAdapter, customToolInstance, agentSpecificTools);
        logger.LogInformation("Registered {Length} custom tools for intent '{Intent}'", agentSpecificTools.Length, intent);

        return morganaToolAdapter;
    }

    /// <summary>
    /// Registers tool methods from a MorganaTool instance into the MorganaToolAdapter.
    /// Uses reflection to create a delegate for each tool method that a definition names.
    /// </summary>
    /// <param name="morganaToolAdapter">Target adapter to register tools into</param>
    /// <param name="toolInstance">
    /// MorganaTool instance containing the tool method implementations.
    /// Can be a base MorganaTool (for base tools) or a derived class like BillingTool (for custom tools).
    /// </param>
    /// <param name="tools">Tool definitions specifying which methods to register from the toolInstance</param>
    private void RegisterToolsInAdapter(
        MorganaToolAdapter morganaToolAdapter,
        MorganaTool toolInstance,
        Records.ToolDefinition[] tools)
    {
        foreach (Records.ToolDefinition toolDefinition in tools)
        {
            MethodInfo? method = toolInstance.GetType().GetMethod(toolDefinition.Name);
            if (method == null)
            {
                logger.LogWarning("Tool '{ToolDefinitionName}' has a definition but no method in {Name}", toolDefinition.Name, toolInstance.GetType().Name);
                continue;
            }

            // Build a strongly-typed delegate whose exact Func<…> type is computed from
            // the method's own ParameterInfo at runtime: a tool is found by reflection over its
            // class, so the concrete delegate type is unknowable at compile time.
            Delegate toolImplementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                [
                    .. method.GetParameters().Select(p => p.ParameterType),
                    method.ReturnType
                ]),
                toolInstance,
                method);

            morganaToolAdapter.AddTool(toolDefinition.Name, toolImplementation, toolDefinition);
        }
    }

    /// <summary>
    /// True when this installation takes part in peer consultation at all — which is also the whole
    /// of what decides whether an agent may be asked a question, since the ring is raised whole and
    /// every agent discovered is published over A2A.
    /// </summary>
    /// <remarks>
    /// Read per agent creation rather than cached: a configuration value belongs to whoever reads it.
    /// </remarks>
    private bool PeerConsultationEnabled
        => configuration.GetValue("Morgana:AgentToAgent:Enabled", true);

    /// <summary>
    /// Composes the agent's two-layer instructions and closes them with the colleagues it holds.
    /// </summary>
    /// <remarks>
    /// The block is appended here rather than inside the composer because which colleagues actually
    /// resolved is known only to this method: the composer is handed a topology, never asked to
    /// discover one. It stays ahead of the per-turn held-context tail, so it rides in the cached
    /// prefix — the roster is fixed for the agent's life, unlike the context it is composed beside.
    /// </remarks>
    /// <param name="agentPrompt">The agent's own domain prompt.</param>
    /// <param name="peerCapable">Whether this agent sits inside the A2A topology at either end.</param>
    /// <param name="peerTerritories">Function name → the colleague's own statement of what falls to it.</param>
    private async Task<string> ComposeInstructionsWithColleaguesAsync(
        Records.Prompt agentPrompt,
        bool peerCapable,
        IReadOnlyDictionary<string, string> peerTerritories)
    {
        string instructions = await promptComposerService.ComposeAgentInstructionsAsync(agentPrompt, peerCapable);
        string? colleagues = await promptComposerService.ComposeColleaguesDeclarationAsync(peerTerritories);

        return colleagues is null ? instructions : $"{instructions}\n{colleagues}\n";
    }

    /// <summary>
    /// Builds one callable function per colleague the agent declares with <c>[ConsultsAgent]</c>.
    /// </summary>
    /// <remarks>
    /// Best-effort as MCP registration is: an unreachable colleague costs that colleague and nothing
    /// more. Startup validation already rejects a declaration naming no agent, so a failure here
    /// means the A2A endpoints are unreachable, not that the topology is wrong.
    /// </remarks>
    /// <param name="agentType">Agent whose <c>[ConsultsAgent]</c> declarations are being honoured</param>
    /// <param name="callerIntent">Asking agent, recorded on the credentials every consultation presents</param>
    /// <param name="conversationId">Conversation the consultations are scoped to, carried as the A2A context id</param>
    /// <param name="sessionAccessor">Hands back the asking agent's live session, which the guards read at invocation</param>
    /// <param name="contextProvider">Context store of the asking agent, holding the per-turn consultation budget</param>
    /// <param name="peerTerritories">Filled with function name → the colleague's own Territory, for the declaration spliced into this agent's instructions</param>
    /// <returns>One AIFunction per resolvable colleague, empty if none is declared</returns>
    private async Task<List<AIFunction>> RegisterPeerAgentsAsync(
        Type agentType,
        string callerIntent,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        MorganaAIContextProvider contextProvider,
        Dictionary<string, string> peerTerritories)
    {
        ConsultsAgentAttribute[] attributes = [.. agentType.GetCustomAttributes<ConsultsAgentAttribute>()];

        if (attributes.Length == 0)
        {
            logger.LogDebug("Agent {AgentTypeName} consults no colleague", agentType.Name);
            return [];
        }

        // The whole mechanism is switchable off in one place: with it disabled an agent runs exactly
        // as it did before, unaware it ever had colleagues, which is what makes the feature safe to
        // turn off in a deployment that cannot afford the extra turns.
        if (!configuration.GetValue("Morgana:AgentToAgent:Enabled", true))
        {
            logger.LogInformation("Peer consultation is disabled: agent {AgentTypeName} will not see its {Count} declared colleague(s)", agentType.Name, attributes.Length);
            return [];
        }

        int maxRoundsPerTurn = configuration.GetValue("Morgana:AgentToAgent:MaxRoundsPerTurn", 4);

        // Every colleague is reached for at once. They are independent agents, often at different
        // systems. Asking them one after another would make this agent's first turn wait out the
        // sum of whatever they each take — a partner that is slow would set the pace for all of them.
        (string FunctionName, AIFunction Function, string Territory)?[] resolvedColleagues =
            await Task.WhenAll(attributes.Select(ResolveColleagueAsync));

        // Reassembled in the order the agent's author declared them: which colleague answered first is
        // a fact about the network and the model's tool list must not reorder itself between runs.
        List<AIFunction> peerAgents = [];
        foreach ((string FunctionName, AIFunction Function, string Territory)? colleague in resolvedColleagues)
        {
            // Unreachable and already reported where the reaching failed: the agent runs without it.
            if (colleague is not (string peerFunctionName, AIFunction peerFunction, string peerTerritory))
                continue;

            peerAgents.Add(peerFunction);

            // The same territory the tool description carries, kept under the callable name: the
            // prompt's ColleaguesDeclaration lists who this agent holds and must name them the way
            // the tool list does.
            peerTerritories[peerFunctionName] = peerTerritory;
        }

        // Whatever was reachable, which is not necessarily everything declared: a colleague that could
        // not be resolved was logged and skipped and the agent runs without it rather than not
        // running at all.
        return peerAgents;

        // One declared colleague, from its published card to the function the model may call. Local to
        // the registration because everything it closes over is this agent's — who is asking, which
        // conversation and the session the guards read at invocation.
        async Task<(string FunctionName, AIFunction Function, string Territory)?> ResolveColleagueAsync(ConsultsAgentAttribute attribute)
        {
            // Resolved through A2A discovery, so what comes back is Microsoft.Agents.AI.A2A's own
            // A2AAgent over the interface the colleague's card advertises — the identical object an
            // agent in another process would obtain for the same colleague. The card comes back with
            // it and it is that fetched card the model is told about: the colleague describes itself,
            // rather than being described by whatever this installation believes about it.
            Records.PeerReference peer = new Records.PeerReference(attribute.Intent, attribute.Instance);

            (AIAgent Agent, AgentCard Card)? resolvedPeer =
                await agentDirectoryService.ResolvePeerAgentAsync(peer, callerIntent);

            if (resolvedPeer is not (AIAgent peerAgent, AgentCard peerCard))
            {
                logger.LogWarning("Agent {AgentTypeName} cannot reach declared colleague '{PeerIntent}'; it will run without it", agentType.Name, attribute.Intent);
                return null;
            }

            // The A2A context identifier is the conversation, bound once here rather than left to a
            // per-call default: every consultation of this colleague belongs to one exchange and it
            // is that id the answering side turns back into the conversation's actor.
            AgentSession peerSession = peerAgent is A2AAgent a2aPeerAgent
                ? await a2aPeerAgent.CreateSessionAsync(conversationId)
                : await peerAgent.CreateSessionAsync();

            // The agent being consulted, named in the guards' refusals to the model and in the trace of
            // the exchange.
            string peerIntent = attribute.Intent;

            // Null for a colleague of this installation: it ran on this conversation and already
            // charged it. It still reports the figure, not knowing its caller is one of its own.
            string? peerSystem = attribute.Instance;

            // Morgana's rules sit above the colleague as pipeline middleware, in the shape the agent
            // framework defines, leaving the resolved A2AAgent untouched. The closure holds nothing
            // that can go stale: it captures immutables only, reads the live session through
            // sessionAccessor() at invocation and never captures the colleague — innerAgent is handed
            // in by the pipeline on every call. One closure per declared colleague per agent and
            // agents are per-conversation, so none is shared. The streaming delegate is left null and
            // the framework bridges streaming onto the run delegate, so the guards cannot be skipped.
            AIAgent guardedPeerAgent = new AIAgentBuilder(peerAgent)
                .Use(async (messages, session, options, innerAgent, cancellationToken) =>
                {
                    // The two rules a consultation is bound by, evaluated against the CALLER's live
                    // session — not the peer session this function was built with — because both are
                    // facts about the turn asking: whether it is itself serving a colleague and how
                    // many rounds it has already spent. Passing means the round is charged.
                    string? refusal = await ApplyPeerGuardsAsync(callerIntent, peerIntent, sessionAccessor(), maxRoundsPerTurn, contextProvider);

                    // Refused and the colleague is never reached: the model gets the refusal in the
                    // very envelope a real answer travels in, so it reads one shape whatever happened
                    // and cannot mistake a rule for a colleague's opinion.
                    if (refusal is not null)
                        return new AgentResponse(new ChatMessage(ChatRole.Assistant, refusal));

                    // The colleague itself, reached through whatever the pipeline handed in. Who is
                    // asking is added to the options here rather than to the question, so the caller's
                    // name travels as metadata and the question stays the caller's own words.
                    AgentResponse peerResponse =
                        await innerAgent.RunAsync(messages, session, WithDeclaredCaller(options, callerIntent), cancellationToken);

                    // Stripped whoever answered, charged only when the ledger was somebody else's.
                    return await SettlePeerDustAsync(peerResponse, conversationId, peerIntent, peerSystem);
                }, null)
                .Build();

            // The function name is what the model calls, so it is derived from the colleague's intent
            // rather than from the card's free-form name and sanitized because a name is constrained
            // where a card's name is not.
            string peerFunctionName = ToFunctionName(peer);

            // The colleague becomes one more callable function in this agent's tool list, bound to the
            // session created above so every call of it belongs to the same A2A exchange. It is offered
            // under its own Territory and nothing else: an inventory of its tools would invite the
            // caller to rule out a question the colleague has never seen.
            AIFunction peerFunction = guardedPeerAgent.AsAIFunction(
                new AIFunctionFactoryOptions
                {
                    Name = peerFunctionName,
                    Description = await promptComposerService.ComposePeerDescriptionAsync(peerCard)
                },
                peerSession);

            logger.LogInformation("Agent {AgentTypeName} may consult '{PeerIntent}'", agentType.Name, attribute.Intent);

            // The territory travels back beside the function. Empty rather than absent for a card with
            // no description — a colleague still exists when it failed to say what it is for.
            return (peerFunctionName, peerFunction, peerCard.Description ?? "");
        }
    }

    /// <summary>
    /// Applies the two rules a consultation is bound by and charges the round to the turn's budget
    /// when they pass.
    /// </summary>
    /// <remarks>
    /// A refusal comes back as an ordinary answer, never an exception: the asking agent is mid-turn
    /// with a user waiting and a refused consultation must degrade its answer, not destroy the turn.
    /// </remarks>
    /// <param name="callerIntent">Asking agent, named in the diagnostics.</param>
    /// <param name="peerIntent">Colleague being consulted, named in the diagnostics.</param>
    /// <param name="callerSession">The asking agent's session, or null when it has none yet.</param>
    /// <param name="maxRoundsPerTurn">Consultations one user turn may spend before the exchange is cut short.</param>
    /// <param name="contextProvider">Context store of the asking agent, holding the turn's round count.</param>
    /// <returns>The serialized refusal envelope, or <c>null</c> when the consultation may proceed.</returns>
    private async Task<string?> ApplyPeerGuardsAsync(
        string callerIntent,
        string peerIntent,
        AgentSession? callerSession,
        int maxRoundsPerTurn,
        MorganaAIContextProvider contextProvider)
    {
        // No session means no turn has run yet, so neither rule has anything to read: there is no
        // consultation in progress to refuse a second hop to and no round count to have exceeded.
        // Letting it through is the only answer that is not invented.
        if (callerSession is null)
            return null;

        // A colleague may not consult a colleague of its own: the chain stops at one hop, so the call
        // graph cannot contain a cycle and no caller chain has to travel with the request.
        if (contextProvider.GetVariable(callerSession, Constants.ContextKeys.ServingConsultation) is not null)
        {
            logger.LogWarning("Agent '{CallerIntent}' attempted to consult '{PeerIntent}' while itself answering a colleague", callerIntent, peerIntent);
            return RefusalEnvelope(await promptComposerService.ComposeToolResultAsync(Constants.ToolResults.ConsultationChained));
        }

        // The second rule: a cap on how many rounds one user turn may spend talking to colleagues.
        // It is a safety net and not the mechanism — convergence is asked of the prose, in the
        // PeerConsultation policy — so hitting it is a warning rather than a fault.
        int roundsSoFar = ReadConsultationRounds(callerSession, contextProvider);
        if (roundsSoFar >= maxRoundsPerTurn)
        {
            logger.LogWarning("Agent '{CallerIntent}' exhausted its {MaxRounds} consultation round(s) for this turn", callerIntent, maxRoundsPerTurn);
            return RefusalEnvelope(await promptComposerService.ComposeToolResultAsync(
                Constants.ToolResults.ConsultationRoundsExhausted,
                new Dictionary<string, string> { [Constants.Placeholders.ConsultationRounds] = roundsSoFar.ToString(CultureInfo.InvariantCulture) }));
        }

        // Charged before the colleague is called, not after it answers: a consultation that hangs or
        // throws has still spent its round and counting only successful ones would let a failing
        // colleague be retried without limit for the whole turn.
        await contextProvider.SetVariableAsync(callerSession, Constants.ContextKeys.ConsultationRounds, roundsSoFar + 1);

        logger.LogInformation("Agent '{CallerIntent}' is consulting '{PeerIntent}' (round {Round})", callerIntent, peerIntent, roundsSoFar + 1);

        // Null is the permission: both rules passed and the round is on the books, so the caller may
        // reach the colleague.
        return null;
    }

    /// <summary>
    /// Returns a copy of the run options declaring who is asking, so nothing outside this call is
    /// mutated. The A2A layer carries the property as message metadata and hands it back to the
    /// answering agent, which is how a consultation names its requester without putting it in the text.
    /// </summary>
    private static AgentRunOptions WithDeclaredCaller(AgentRunOptions? options, string callerIntent)
    {
        AgentRunOptions declaredOptions = options?.Clone() ?? new AgentRunOptions();

        declaredOptions.AdditionalProperties ??= [];
        declaredOptions.AdditionalProperties[Constants.MessageProperties.CallerIntent] = callerIntent;

        return declaredOptions;
    }

    /// <summary>
    /// Reads the asking agent's consultation counter for the current turn, tolerating the
    /// <see cref="JsonElement"/> form a value takes once its session has been persisted and reloaded.
    /// </summary>
    private static int ReadConsultationRounds(AgentSession callerSession, MorganaAIContextProvider contextProvider)
        => contextProvider.GetVariable(callerSession, Constants.ContextKeys.ConsultationRounds) switch
        {
            int rounds => rounds,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetInt32(),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0
        };

    /// <summary>
    /// Takes what a colleague reports having spent off its answer and charges it home when that
    /// colleague was published elsewhere.
    /// </summary>
    /// <remarks>
    /// Two independent halves. Stripping is unconditional: a model given a number will narrate it and
    /// every colleague reports one. Charging is not: one of ours already billed this conversation.
    /// An envelope that will not parse is handed back untouched rather than made to fail.
    /// </remarks>
    /// <param name="peerResponse">The colleague's answer, carrying the serialized envelope.</param>
    /// <param name="conversationId">Conversation charged for what the colleague spent.</param>
    /// <param name="peerIntent">Colleague consulted, named in the metric.</param>
    /// <param name="peerSystem">System publishing it, named in the metric beside the intent; null for a colleague of this installation.</param>
    private async Task<AgentResponse> SettlePeerDustAsync(
        AgentResponse peerResponse,
        string conversationId,
        string peerIntent,
        string? peerSystem)
    {
        try
        {
            // No side channel and none wanted: Morgana's own JSON at both ends, carried by any host.
            Records.PeerConsultationResponse? envelope = JsonSerializer.Deserialize<Records.PeerConsultationResponse>(
                peerResponse.Text, Records.DefaultJsonSerializerOptions);

            // Nothing to hide: an installation that does not report is left as it arrived.
            if (envelope?.DustConsumed is not > 0)
                return peerResponse;

            // Attributed to the agent and system that burned it, so the spend stays readable as a bill.
            if (peerSystem is not null)
                await dustLimitService.ChargeAsync(
                    conversationId, envelope.DustConsumed.Value, $"{Constants.Morgana} ({peerIntent}@{peerSystem})");

            // The model reads this message's text, so the figure has to be gone from the text itself.
            return new AgentResponse(new ChatMessage(
                ChatRole.Assistant,
                JsonSerializer.Serialize(envelope with { DustConsumed = null }, Records.DefaultJsonSerializerOptions)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read what '{PeerIntent}' at '{PeerSystem}' spent; its answer stands and the charge is lost", peerIntent, peerSystem ?? Constants.Morgana);
            return peerResponse;
        }
    }

    /// <summary>Renders a refusal in the envelope a colleague's real answer travels in.</summary>
    private static string RefusalEnvelope(string message)
        => JsonSerializer.Serialize(new Records.PeerConsultationResponse(message, false), Records.DefaultJsonSerializerOptions);

    /// <summary>Builds the name under which a colleague is offered as a callable function.</summary>
    /// <remarks>
    /// Intents are authored freely while a function name is not, so anything outside the permitted
    /// alphabet folds to an underscore; the prefix keeps a colleague visibly distinct from the
    /// agent's own tools in the model's tool list. A colleague published by an instance carries that
    /// instance in the name, so an agent may hold two colleagues handling the same intent at two agents
    /// without them colliding. Public because the startup check must derive the same name: instance
    /// names are written by people and two of them can fold to one function, which is a startup error
    /// rather than something to discover when the provider rejects the tool list.
    /// </remarks>
    /// <param name="peer">Colleague being offered.</param>
    public static string ToFunctionName(Records.PeerReference peer)
    {
        string peerName = peer.Instance is null ? peer.Intent : $"{peer.Instance}_{peer.Intent}";

        return $"{Constants.AgentToAgent.PeerFunctionNamePrefix}{new string([.. peerName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')])}";
    }

    /// <summary>
    /// Discovers tools from every MCP server declared on the agent.
    /// Collects [UsesMCPServer] attributes and discovers tools from each.
    /// McpClientTool instances are already AIFunctions; no schema conversion applied.
    /// </summary>
    /// <param name="agentType">Agent type to inspect for [UsesMCPServer] attributes</param>
    /// <param name="takenNames">The names that the agent's other tools already hold, which an MCP tool may not take</param>
    /// <returns>Discovered tools as AIFunctions, empty if no servers declared</returns>
    private async Task<List<AIFunction>> RegisterMCPToolsAsync(Type agentType, IEnumerable<string> takenNames)
    {
        // An agent may declare several [UsesMCPServer] (multiple servers, mixed
        // Http/Stdio) — collect them all, not just the first.
        UsesMCPServerAttribute[] attributes = [.. agentType.GetCustomAttributes<UsesMCPServerAttribute>()];

        // No MCP on this agent is the common, expected case (native-tool or tool-less
        // agents) — Debug, not Warning: it is not a problem, just not applicable.
        if (attributes.Length == 0)
        {
            logger.LogDebug("Agent {AgentTypeName} does not use MCP servers", agentType.Name);
            return [];
        }

        logger.LogInformation("Agent {AgentTypeName} declares {AttributesLength} MCP server(s)", agentType.Name, attributes.Length);

        List<AIFunction> mcpTools = [];
        HashSet<string> heldNames = new(takenNames, StringComparer.Ordinal);

        foreach (UsesMCPServerAttribute attribute in attributes)
        {
            // Per-server isolation is the whole point of this loop: each server is
            // attempted independently and a failure (unreachable host, bad URI, discovery
            // error) is logged and swallowed so it cannot abort the remaining servers or
            // agent creation. This is what makes MCP registration "best-effort" — a dead
            // server costs that server's tools, nothing more.
            try
            {
                foreach (AIFunction discovered in await DiscoverMCPToolsFromServerAsync(attribute))
                {
                    // The tool loop refuses a tool list that names two tools alike, which would fail every turn of the agent.
                    if (heldNames.Contains(discovered.Name)
                        || discovered.Name.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.Ordinal))
                    {
                        logger.LogError(
                            "Agent {AgentTypeName} drops tool '{McpToolName}' of MCP server '{ServerCommand}': its name is taken by another tool or reserved for colleagues",
                            agentType.Name, discovered.Name, attribute.Command);
                        continue;
                    }

                    heldNames.Add(discovered.Name);
                    mcpTools.Add(discovered);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register MCP tools from server: {AttributeCommand}", attribute.Command);
            }
        }

        return mcpTools;
    }

    /// <summary>
    /// Projects a discovered MCP tool into the definition that workflow validation and the engine read.
    /// </summary>
    /// <remarks>
    /// The MCP layer never asks the user for approval, so the definition never requires it.
    /// </remarks>
    private static Records.ToolDefinition ProjectMCPTool(AIFunction mcpTool)
    {
        IReadOnlyList<Records.ToolParameter> parameters = [];
        if (mcpTool.JsonSchema.ValueKind == JsonValueKind.Object && mcpTool.JsonSchema.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> required = mcpTool.JsonSchema.TryGetProperty("required", out JsonElement requiredNames) && requiredNames.ValueKind == JsonValueKind.Array
                ? [.. requiredNames.EnumerateArray().Select(name => name.GetString() ?? string.Empty)]
                : [];

            parameters = [.. properties.EnumerateObject().Select(property => new Records.ToolParameter(
                property.Name,
                DescriptionOf(property.Value),
                required.Contains(property.Name),
                Constants.Scopes.Request))];
        }

        // A server declaring no output schema leaves Returns absent: nothing can be carried out of such a tool.
        IReadOnlyList<Records.ToolReturn>? returns = null;
        if (mcpTool.ReturnJsonSchema is { ValueKind: JsonValueKind.Object } returnSchema
            && returnSchema.TryGetProperty("properties", out JsonElement returnProperties)
            && returnProperties.ValueKind == JsonValueKind.Object)
        {
            returns = [.. returnProperties.EnumerateObject().Select(property => new Records.ToolReturn(
                property.Name,
                DescriptionOf(property.Value),
                string.Equals(property.Name, Constants.Workflows.FailureField, StringComparison.OrdinalIgnoreCase)))];
        }

        return new Records.ToolDefinition(mcpTool.Name, mcpTool.Description, parameters, Returns: returns);
    }

    /// <summary>The description that a JSON schema property carries; empty when it has none.</summary>
    private static string DescriptionOf(JsonElement schemaProperty)
        => schemaProperty.ValueKind == JsonValueKind.Object
           && schemaProperty.TryGetProperty("description", out JsonElement description)
           && description.ValueKind == JsonValueKind.String
            ? description.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Weighs the workflows of an agent that holds MCP tools against the tools it really holds.
    /// </summary>
    /// <returns>The workflows that hold; each other one is logged with every reason and left out.</returns>
    private List<Records.WorkflowDefinition> KeepSoundWorkflows(
        string intent,
        Type agentType,
        IReadOnlyList<Records.WorkflowDefinition> workflows,
        IReadOnlyList<Records.ToolDefinition> heldTools)
    {
        List<Records.WorkflowDefinition> kept = [];

        foreach (Records.WorkflowDefinition workflow in workflows)
        {
            List<string> errors = HandlesIntentAgentRegistryService.ValidateWorkflows(intent, [workflow], heldTools, usesMcpServer: false);
            if (errors.Count == 0)
            {
                kept.Add(workflow);
                continue;
            }

            // A server that was down at birth leaves its tools unheld, so its workflows fall here as lacking a tool: that is intended.
            logger.LogError(
                "Workflow '{Workflow}' of intent '{Intent}' (agent {AgentTypeName}) is withdrawn for this conversation: {Reasons}",
                workflow.Name, intent, agentType.Name, string.Join("; ", errors));
        }

        return kept;
    }

    /// <summary>
    /// Discovers tools from a single MCP server. Tools retain server-declared names and schema
    /// untouched and survive the server ending its session for the whole life of the agent, which
    /// discovers them only once, at creation.
    /// </summary>
    /// <param name="serverAttribute">Attribute declaring the MCP server (transport, command, args)</param>
    /// <returns>Server's tools as AIFunctions</returns>
    private async Task<IList<AIFunction>> DiscoverMCPToolsFromServerAsync(UsesMCPServerAttribute serverAttribute)
    {
        logger.LogInformation("Registering MCP tools from server: {ServerAttributeCommand}", serverAttribute.Command);

        IList<AIFunction> mcpTools = await imcpClientRegistryService.DiscoverResilientToolsAsync(serverAttribute);

        // A reachable server that advertises zero tools is not an error (it may expose
        // none yet, or only prompts/resources): warn for visibility and return — there is
        // simply nothing to bind and the agent keeps its base/native tools.
        if (mcpTools.Count == 0)
        {
            logger.LogWarning("No tools discovered from MCP server: {ServerAttributeCommand}", serverAttribute.Command);
            return [];
        }

        foreach (AIFunction mcpTool in mcpTools)
            logger.LogInformation("Registered MCP tool: {McpToolName}", mcpTool.Name);

        logger.LogInformation("Successfully registered {McpToolsCount} MCP tools from {ServerAttributeCommand}", mcpTools.Count, serverAttribute.Command);

        return [.. mcpTools];
    }

    /// <summary>
    /// What one agent that declares workflows holds to run them: the engine, the declarations and the session
    /// that the position is kept in.
    /// </summary>
    /// <param name="Engine">The state machine of the agent's workflows.</param>
    /// <param name="Definitions">The workflows the agent declares.</param>
    /// <param name="Tools">The agent's domain tools and MCP tools by name, which say which need approval.</param>
    /// <param name="MCPToolNames">The names of the tools that come from an MCP server, whose results are read as the protocol's envelope.</param>
    /// <param name="ContextProvider">The store the position is written to.</param>
    /// <param name="SessionAccessor">Returns the agent's current session.</param>
    private sealed record AgentWorkflows(
        WorkflowEngine Engine,
        IReadOnlyList<Records.WorkflowDefinition> Definitions,
        IReadOnlyDictionary<string, Records.ToolDefinition> Tools,
        IReadOnlySet<string> MCPToolNames,
        MorganaAIContextProvider ContextProvider,
        Func<AgentSession?> SessionAccessor);

    private class ToolDefinitionNameComparer : IEqualityComparer<Records.ToolDefinition>
    {
        public bool Equals(Records.ToolDefinition? x, Records.ToolDefinition? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;
            return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(Records.ToolDefinition obj) =>
            obj.Name.GetHashCode(StringComparison.OrdinalIgnoreCase);
    }
}