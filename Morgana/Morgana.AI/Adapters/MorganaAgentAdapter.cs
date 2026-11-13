using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.AI.Services;
using Morgana.AI.Tools;

namespace Morgana.AI.Adapters;

// IChatReducer is marked experimental (MEAI001); the history provider every agent is built with takes one.
#pragma warning disable MEAI001

/// <summary>
/// Builds the agent behind each Morgana agent: its composed instructions, its tools, MCP tools and colleagues, its providers and its tier's metered client.
/// Tools reach the session of the turn in flight through a factory evaluated on every call, since the adapter holds no actor.
/// </summary>
public class MorganaAgentAdapter
{
    /// <summary>Resolves the agent's domain prompt from agents.json.</summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>Supplies the chat client and dust pricing of the tier each agent's <c>[RequiresLLMTier]</c> declares.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Finds the tool class of an intent; none for an agent served by MCP alone or by the base tools.</summary>
    private readonly IToolRegistryService toolRegistryService;

    /// <summary>Lists an MCP server's tools when the tool guard is off.</summary>
    private readonly IMCPClientRegistryService imcpClientRegistryService;

    /// <summary>Builds the reducer of an agent's history from the deployment's settings.</summary>
    private readonly HistoryReducerService chatReducerService;

    /// <summary>The ledger an agent's own calls and its reducer's are charged to.</summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>Resolves each declared colleague into a callable agent together with its card.</summary>
    private readonly IAgentDirectoryService agentDirectoryService;

    /// <summary>
    /// Screens every tool result the agent's model is about to read and admits what MCP servers and partners offer.
    /// A result that conceals a purpose against the assistant stays out of the tool loop and out of the session that would replay it.
    /// </summary>
    private readonly IGuardRailService guardRailService;

    /// <summary>The application configuration, read for the consultation budget and the two switches.</summary>
    private readonly IConfiguration configuration;

    /// <summary>Records each agent's creation, its tools and the sources it ran without.</summary>
    private readonly ILogger logger;

    /// <summary>The base tools every agent holds ahead of its own, projected from <see cref="MorganaTool"/>.</summary>
    private readonly Records.ToolDefinition[] morganaTools;

    /// <summary>
    /// Composes everything the agent's model reads: its instructions, its tool descriptions and the colleagues' declaration.
    /// It is passed on to <see cref="MorganaToolAdapter"/> and <see cref="MorganaAIContextProvider"/>, which compose their own fragments through it.
    /// </summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>Binds the adapter to the services an agent is built from.</summary>
    /// <param name="llmService">Supplies each tier's chat client and pricing.</param>
    /// <param name="promptResolverService">Resolves the domain prompts.</param>
    /// <param name="promptComposerService">Composes instructions and tool descriptions.</param>
    /// <param name="toolRegistryService">Finds an intent's tool class.</param>
    /// <param name="imcpClientRegistryService">Lists MCP servers' tools.</param>
    /// <param name="chatReducerService">Builds the history reducer.</param>
    /// <param name="dustLimitService">The ledger agents are charged to.</param>
    /// <param name="agentDirectoryService">Resolves declared colleagues.</param>
    /// <param name="guardRailService">Screens tool results and admits offers.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="logger">Records each agent's creation.</param>
    public MorganaAgentAdapter(
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IPromptComposerService promptComposerService,
        IToolRegistryService toolRegistryService,
        IMCPClientRegistryService imcpClientRegistryService,
        HistoryReducerService chatReducerService,
        IDustLimitService dustLimitService,
        IAgentDirectoryService agentDirectoryService,
        IGuardRailService guardRailService,
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
        this.guardRailService = guardRailService;
        this.configuration = configuration;
        this.logger = logger;

        // The base tools are the same for every agent: the methods of MorganaTool describe them, so they are read once here.
        morganaTools = [.. MorganaToolAdapter.ProjectToolDefinitions(typeof(MorganaTool))];
    }

    /// <summary>Builds the agent of <paramref name="agentType"/> for one conversation, with the providers its turns read and write.</summary>
    /// <param name="agentType">The agent class, carrying <c>[HandlesIntent]</c> and <c>[RequiresLLMTier]</c>.</param>
    /// <param name="conversationId">The conversation the agent serves.</param>
    /// <param name="sessionAccessor">Returns the agent's session of the turn in flight; <c>null</c> before the first turn.</param>
    /// <param name="sharedContextCallback">Files a shared variable's value in the conversation's <c>shared_context</c>.</param>
    /// <returns>The agent with its context and history providers.</returns>
    internal Records.CreatedAgent CreateAgent(
        Type agentType,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        Func<string, object, Task> sharedContextCallback)
        // The one point where the creation path waits synchronously: Akka materializes a MorganaAgent inside its
        // constructor through DependencyResolver.Props, which offers no asynchronous seam.
        => CreateAgentAsync(agentType, conversationId, sessionAccessor, sharedContextCallback)
            .GetAwaiter()
            .GetResult();

    /// <summary>The body of <see cref="CreateAgent"/>, where prompt resolution, composition and tool discovery are awaited.</summary>
    private async Task<Records.CreatedAgent> CreateAgentAsync(
        Type agentType,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        Func<string, object, Task> sharedContextCallback)
    {
        // 1) Identity: the [HandlesIntent] attribute is the agent's contract. Its absence
        //    is a wiring bug (a MorganaAgent subclass that forgot the attribute), so fail
        //    loud at creation rather than silently producing an unroutable agent.
        HandlesIntentAttribute? intentAttribute = agentType.GetCustomAttribute<HandlesIntentAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [HandlesIntent] attribute");

        // 1b) Tier: the model class the agent declares it needs. The startup checks already refused an agent
        //     without it, so the throw only guards a type that reached here around them.
        RequiresLLMTierAttribute tierAttribute = agentType.GetCustomAttribute<RequiresLLMTierAttribute>()
            ?? throw new InvalidOperationException($"Agent type '{agentType.Name}' must be decorated with [RequiresLLMTier] attribute");

        logger.LogInformation("Creating agent for intent '{IntentAttributeIntent}' on tier '{Tier}'...", intentAttribute.Intent, tierAttribute.Tier);

        // 2) Domain prompt for this intent (instructions/personality/formatting/territory),
        //    resolved from agents.json.
        Records.Prompt agentPrompt = await promptResolverService.ResolveAsync(intentAttribute.Intent);

        // 3) Tool surface = the base tools followed by the tools the intent's tool class declares.
        //    A domain tool named like a base tool is refused at startup, so the two never overlap.
        Type? toolType = toolRegistryService.FindToolTypeForIntent(intentAttribute.Intent);
        Records.ToolDefinition[] domainTools = toolType is null ? [] : [.. MorganaToolAdapter.ProjectToolDefinitions(toolType)];
        Records.ToolDefinition[] agentTools = [.. morganaTools, .. domainTools];

        // 4) The agent's variables, its shared ones filed in the conversation's registry through sharedContextCallback.
        MorganaAIContextProvider morganaAIContextProvider = CreateAIContextProvider(
            intentAttribute.Intent,
            agentTools,
            sharedContextCallback);

        // 5) ToolContext factory, evaluated on each tool call and never now: the adapter holds no actor, so the
        //    session of the turn in flight is read through sessionAccessor at call time. A null there means the
        //    agent ran without ExecuteAgentAsync setting its session, a wiring error the tool cannot recover from.
        Func<MorganaTool.ToolContext> toolContextFactory = () =>
        {
            AgentSession session = sessionAccessor()
                ?? throw new InvalidOperationException(
                    $"Agent '{intentAttribute.Intent}' has no active session during tool execution. " +
                    $"Ensure ExecuteAgentAsync sets aiAgentSession before invoking the agent.");

            // The conversation id travels with the session: the one identifier a domain tool may scope state to that the model can never write.
            return new MorganaTool.ToolContext(morganaAIContextProvider, session, conversationId);
        };

        // 6a) The base tools and the intent's own tool class, bound to the methods that implement them.
        MorganaToolAdapter morganaToolAdapter = CreateToolAdapterForIntent(
            intentAttribute.Intent,
            toolType,
            domainTools,
            toolContextFactory);

        // 6b) The tools of every [UsesMCPServer], each arriving already a function. A server that is down costs its
        //     own tools and never the agent. MCP tools enter last, so one whose name collides with a tool that
        //     entered before is refused; with the tool guard on, they are the ones the guard admitted.
        Dictionary<string, UsesMCPServerAttribute> mcpToolServers = [];
        List<AIFunction> mcpTools = await RegisterMCPToolsAsync(agentType, intentAttribute.Intent, [.. agentTools.Select(tool => tool.Name)], mcpToolServers);

        // 6c) Collect the colleagues this agent declares it may consult. Like MCP tools they arrive
        //     already AIFunctions and bypass the native adapter entirely — they are not declared
        //     by any tool class, are not implemented by any MorganaTool and their prose is the colleague's
        //     own card rather than something this agent's author wrote.
        Dictionary<string, string> peerTerritories = [];
        List<AIFunction> peerAgents = await RegisterPeerAgentsAsync(
            agentType,
            intentAttribute.Intent,
            conversationId,
            sessionAccessor,
            morganaAIContextProvider,
            peerTerritories);

        // 7) The agent's own tier, metered under a role naming the agent and its tier, such as "Morgana (Billing/Efficiency)".
        //    The role qualifies the one the pipeline charges under, so a ledger grouped by prefix keeps one installation together.
        string intent = intentAttribute.Intent;
        string dustRole = $"{Constants.Morgana} ({char.ToUpperInvariant(intent[0])}{intent[1..]}/{tierAttribute.Tier})";
        IChatClient tierChatClient = llmService.GetChatClient(tierAttribute.Tier);
        Records.MagicDustPricing tierPricing = llmService.GetMorganaDust(tierAttribute.Tier);
        IChatClient agentChatClient =
            new DustAccountingChatClient(tierChatClient, dustLimitService, tierPricing, dustRole, conversationId);

        // 8) The history provider keeps the whole transcript in the session and hands the model the reducer's view.
        //    The reducer runs on the metered client, so its summaries are charged like every other call.
        IChatReducer? chatReducer = chatReducerService.CreateReducer(agentChatClient);
        MorganaChatHistoryProvider chatHistoryProvider = new MorganaChatHistoryProvider(intentAttribute.Intent, chatReducer, logger);

        // 9) The agent over the metered client: its providers, an id stable for the conversation, the
        //    two-layer instructions and every tool, the agent's own ahead of MCP's and the colleagues'.
        AIAgent aiAgent = agentChatClient.AsAIAgent(
            new ChatClientAgentOptions
            {
                // The variables and the per-turn declarations
                AIContextProviders = [morganaAIContextProvider],

                // The transcript and the model's view of it
                ChatHistoryProvider = chatHistoryProvider,

                // The id the agent is known by in this conversation and the intent it serves
                Id = $"{intentAttribute.Intent.ToLowerInvariant()}-{conversationId}",
                Name = intentAttribute.Intent,

                // What the model reads and what it may call
                ChatOptions = new ChatOptions
                {
                    // The two layers, closed by the colleagues' declaration where the agent holds any
                    Instructions = await ComposeInstructionsWithColleaguesAsync(
                        agentPrompt,

                        // Both ends of the topology, not just the asking one: an agent that consults
                        // nobody still reads the peer-consultation rules, because with the ring raised
                        // whole a colleague's question can land on it at any turn.
                        PeerConsultationEnabled,
                        peerTerritories),

                    // MCP tools and colleagues come last, after the names they may not take
                    Tools = [.. await morganaToolAdapter.CreateAllFunctionsAsync(), .. mcpTools, .. peerAgents]
                }
            });

        // 9b) Every tool result passes the guard before the model reads it. Switched off, the agent
        //     is exactly the one assembled above.
        if (ToolGuardrailEnabled)
            aiAgent = GuardToolResults(aiAgent, agentType, intentAttribute.Intent, mcpToolServers, conversationId, sessionAccessor, morganaAIContextProvider);

        // 10) The providers travel with the agent, since MorganaAgent reads and writes them outside a run.
        return new Records.CreatedAgent(aiAgent, morganaAIContextProvider, chatHistoryProvider);
    }

    /// <summary>Builds the agent's context provider, knowing which of its tools' parameters are shared with the other agents.</summary>
    /// <param name="agentName">The agent's intent, named in the log line.</param>
    /// <param name="tools">The agent's tool definitions, read for shared context parameters.</param>
    /// <param name="sharedContextCallback">Files a shared variable's value in the conversation's <c>shared_context</c>.</param>
    private MorganaAIContextProvider CreateAIContextProvider(
        string agentName,
        IEnumerable<Records.ToolDefinition> tools,
        Func<string, object, Task> sharedContextCallback)
    {
        // A parameter is shared only when it is both flagged shared and context-scoped: a request-scoped one is
        // asked every turn, so filing it first-write-wins would freeze a per-turn input. One variable is usually
        // declared on several tools, so each name is taken once.
        List<string> sharedVariables = [.. tools
             .SelectMany(t => t.Parameters)
             .Where(p => p.Shared && string.Equals(p.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
             .Select(p => p.Name)
             .Distinct()];

        logger.LogInformation(
            sharedVariables.Count > 0
                ? $"Agent '{agentName}' has {sharedVariables.Count} shared variables: {string.Join(", ", sharedVariables)}"
                : $"Agent '{agentName}' has NO shared variables");

        // Only a write to a name in the shared set reaches the registry. The composer goes with the provider because the
        // held-context declaration names what the session holds at each turn, which nobody knows at creation time.
        return new MorganaAIContextProvider(logger, promptComposerService, sharedVariables)
        {
            // The agent's own filing of a shared value in the conversation's registry
            OnSharedContextUpdate = sharedContextCallback
        };
    }

    /// <summary>Binds the base tools and the intent's own tool class to the AIFunctions the agent's model calls.</summary>
    /// <param name="intent">Agent intent name.</param>
    /// <param name="toolType">The intent's tool class; null when the intent declares none.</param>
    /// <param name="domainTools">The tool definitions projected from the intent's tool class.</param>
    /// <param name="toolContextFactory">Returns the provider, session and conversation of the turn in flight to every tool.</param>
    /// <returns>The adapter holding every native tool of the agent.</returns>
    private MorganaToolAdapter CreateToolAdapterForIntent(
        string intent,
        Type? toolType,
        Records.ToolDefinition[] domainTools,
        Func<MorganaTool.ToolContext> toolContextFactory)
    {
        // The adapter composes the description of each generated AIFunction through the composer,
        // which splices ToolDescriptionContextGuidance into the tools declaring context-scoped
        // parameters. Parameter descriptions carry no framework template at all — see
        // MorganaToolAdapter.CreateFunctionAsync.
        MorganaToolAdapter morganaToolAdapter = new MorganaToolAdapter(promptComposerService);

        // Every agent holds the base tools, an MCP-only or tool-less one included.
        MorganaTool baseTool = new MorganaTool(logger, toolContextFactory);
        morganaToolAdapter.AddTools(baseTool, morganaTools);
        logger.LogInformation("Registered {BaseToolsLength} base tools for intent '{Intent}'", morganaTools.Length, intent);

        // An agent with no tool class holds the base tools alone (MCP and colleagues aside).
        if (toolType is null)
        {
            logger.LogInformation("No intent-specific tools defined for intent '{Intent}' (agent has base tools only)", intent);
            return morganaToolAdapter;
        }

        logger.LogInformation("Found custom native tool: {ToolTypeName} for intent '{Intent}' via ToolRegistry", toolType.Name, intent);

        // A tool class that cannot be built is an authoring fault, almost always a constructor other than
        // (ILogger, Func<MorganaTool.ToolContext>): the agent is refused rather than shipped without its domain tools.
        MorganaTool customToolInstance;
        try
        {
            // One instance per agent and so per conversation, so a tool class never carries state across conversations.
            customToolInstance = (MorganaTool)Activator.CreateInstance(toolType, logger, toolContextFactory)!;
        }
        catch (Exception ex)
        {
            // The exception names the constructor the class must offer.
            logger.LogError(ex, "Failed to instantiate custom tool {ToolTypeName} for intent '{Intent}'", toolType.Name, intent);
            throw new InvalidOperationException(
                $"Could not create custom tool instance for intent '{intent}'. " +
                $"Ensure {toolType.Name} has a constructor accepting " +
                $"(ILogger, Func<MorganaTool.ToolContext>).", ex);
        }

        // The domain definitions bind to the domain instance, the base ones being already bound to the base instance.
        morganaToolAdapter.AddTools(customToolInstance, domainTools);
        logger.LogInformation("Registered {Length} custom tools for intent '{Intent}'", domainTools.Length, intent);

        // The base tools ahead of the domain's, as the model reads them.
        return morganaToolAdapter;
    }

    /// <summary>
    /// True when this installation takes part in peer consultation at all.
    /// It alone decides whether an agent may be asked a question, since every agent discovered is published over A2A.
    /// </summary>
    private bool PeerConsultationEnabled
        => configuration.GetValue("Morgana:AgentToAgent:Enabled", true);

    /// <summary>
    /// True when tool results are screened before the agent's model reads them.
    /// </summary>
    private bool ToolGuardrailEnabled
        => configuration.GetValue("Morgana:ActorSystem:EnableToolGuardrail", true);

    /// <summary>
    /// Wraps the agent so that every tool result but the base tools' and a local colleague's passes the guard before the model reads it, a refused one replaced by the fact of its quarantine.
    /// </summary>
    /// <param name="agent">The agent as assembled, whose tool loop is to be guarded.</param>
    /// <param name="agentType">The agent's class, whose declared colleagues are exempt from the guard.</param>
    /// <param name="agentIntent">The intent the agent serves, tagged on every security event it records.</param>
    /// <param name="mcpToolServers">The MCP tools the agent was given with the server each came from; they are answered from outside the installation and only while their server is available and secure.</param>
    /// <param name="conversationId">The conversation the agent serves, named in the guard's findings.</param>
    /// <param name="sessionAccessor">Hands back the agent's live session, where the quarantine is recorded.</param>
    /// <param name="contextProvider">Reads and writes the quarantine on the live session.</param>
    private AIAgent GuardToolResults(
        AIAgent agent,
        Type agentType,
        string agentIntent,
        IReadOnlyDictionary<string, UsesMCPServerAttribute> mcpToolServers,
        string conversationId,
        Func<AgentSession?> sessionAccessor,
        MorganaAIContextProvider contextProvider)
    {
        // The base tools hand back values that already crossed a guarded door and a colleague of this
        // installation answers from its own guarded tools: neither is screened again.
        HashSet<string> unguardedToolNames =
        [
            .. morganaTools.Select(tool => tool.Name),
            .. agentType.GetCustomAttributes<ConsultsAgentAttribute>()
                .Where(attribute => attribute.Instance is null)
                .Select(attribute => ToFunctionName(new Records.PeerReference(attribute.Intent, null)))
        ];

        // The partners' colleagues by the function name the model calls them under, for the go/no-go read before each call.
        Dictionary<string, Records.PeerReference> partnerToolPeers = agentType.GetCustomAttributes<ConsultsAgentAttribute>()
            .Where(attribute => attribute.Instance is not null)
            .Select(attribute => new Records.PeerReference(attribute.Intent, attribute.Instance))
            .ToDictionary(ToFunctionName, peer => peer);

        // An MCP server and a partner answer from outside the installation: a call to them is sent only while their source is available and secure.
        HashSet<string> externalToolNames = [.. mcpToolServers.Keys, .. partnerToolPeers.Keys];

        // The guard joins the agent's tool loop through the agent framework's own function-invocation
        // middleware, which wraps every call of every run, streamed or not.
        return new AIAgentBuilder(agent)
            .Use(async (_, invocation, next, cancellationToken) =>
            {
                // The function the model called.
                string toolName = invocation.Function.Name;

                // An unguarded tool runs and answers as it always did.
                if (unguardedToolNames.Contains(toolName))
                    return await next(invocation, cancellationToken);

                // The session where this turn's quarantine is recorded; absent when the agent has none yet.
                AgentSession? session = sessionAccessor();

                // A tool already refused in this turn is not run again: whatever it would return is
                // withheld the same way.
                if (session is not null && ReadQuarantinedTools(session, contextProvider).Contains(toolName, StringComparer.Ordinal))
                {
                    logger.LogInformation(
                        "Tool {ToolName} withheld without running for conversation {ConversationId}: its result was withheld earlier in this turn",
                        toolName, conversationId);

                    // Counts the tool that was kept from running: the model asked again for a result already refused.
                    Telemetry.SecurityEvents.Add(1,
                        new KeyValuePair<string, object?>(Telemetry.SecurityDoor, Telemetry.SecurityDoors.Tool),
                        new KeyValuePair<string, object?>(Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.WithheldWithoutRunning),
                        new KeyValuePair<string, object?>(Telemetry.AgentIntent, agentIntent));

                    // The event lands on the span current while the tool runs (the agent's own or a child of it)
                    // so the trace of the turn shows what was withheld and why without the text itself.
                    Activity.Current?.AddEvent(new ActivityEvent(
                        Telemetry.EventToolWithheld,
                        tags: new ActivityTagsCollection
                        {
                            [Telemetry.ToolName] = toolName,
                            [Telemetry.SecurityOutcome] = Telemetry.SecurityOutcomes.WithheldWithoutRunning
                        }));

                    // The model reads the same fact of the quarantine it read the first time.
                    return await promptComposerService.ComposeQuarantinedResultAsync(toolName);
                }

                // An agent may still hold the snapshot of a source that has since been refused: its call is not sent.
                if (externalToolNames.Contains(toolName) && !SourceIsAvailableAndSecure(toolName))
                {
                    logger.LogWarning(
                        "Tool {ToolName} not sent for conversation {ConversationId}: its source is not available and secure",
                        toolName, conversationId);

                    // Counts the call that was kept from leaving.
                    Telemetry.SecurityEvents.Add(1,
                        new KeyValuePair<string, object?>(Telemetry.SecurityDoor, Telemetry.SecurityDoors.Tool),
                        new KeyValuePair<string, object?>(Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.NotSent),
                        new KeyValuePair<string, object?>(Telemetry.AgentIntent, agentIntent));

                    // The event lands on the span current while the tool would have run, without the arguments.
                    Activity.Current?.AddEvent(new ActivityEvent(
                        Telemetry.EventToolWithheld,
                        tags: new ActivityTagsCollection
                        {
                            [Telemetry.ToolName] = toolName,
                            [Telemetry.SecurityOutcome] = Telemetry.SecurityOutcomes.NotSent
                        }));

                    // The call fails as a call to an unreachable server fails: the model reads the tool loop's own
                    // error and no text of ours. The arguments, which may carry the user's data, never leave.
                    throw new InvalidOperationException($"The source of {toolName} is not available and secure.");
                }

                // The tool runs; an exception is the tool loop's to handle as it always was.
                object? result = await next(invocation, cancellationToken);

                // The tool loop hands results on as JSON (a native tool's string and a colleague's answer
                // arrive as a JSON element and an MCP tool's as a content block) so the guard reads what
                // the provider would.
                string resultText = JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);

                // The name-value pairs the model called the tool with, in the escaping the tool loop hands to the
                // provider; the inspector judges the result against them.
                string argumentsText = JsonSerializer.Serialize(new Dictionary<string, object?>(invocation.Arguments), AIJsonUtilities.DefaultOptions);

                // The guard's verdict on what the tool returned.
                Records.GuardRailResult verdict = await guardRailService.CheckToolResultAsync(conversationId, toolName, argumentsText, resultText, cancellationToken);

                // A compliant result reaches the model untouched.
                if (verdict.Compliant)
                    return result;

                // The withheld result by the layer that decided it.
                TagList securityTags = new TagList
                {
                    { Telemetry.SecurityDoor, Telemetry.SecurityDoors.Tool },
                    { Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.Withheld },
                    { Telemetry.SecuritySource, verdict.Source.ToString() },
                    { Telemetry.AgentIntent, agentIntent }
                };

                // The technical cause is published only when the result was withheld for want of a judgment.
                if (verdict.ErrorType is not null)
                    securityTags.Add(Telemetry.ErrorType, verdict.ErrorType);

                // Counts the withheld result at the tools' door.
                Telemetry.SecurityEvents.Add(1, securityTags);

                // The event lands on the span current while the tool runs (the agent's own or a child of it)
                // so the trace of the turn shows what was withheld and why without the text itself.
                Activity.Current?.AddEvent(new ActivityEvent(
                    Telemetry.EventToolWithheld,
                    tags: new ActivityTagsCollection
                    {
                        [Telemetry.ToolName] = toolName,
                        [Telemetry.SecurityOutcome] = Telemetry.SecurityOutcomes.Withheld,
                        [Telemetry.SecuritySource] = verdict.Source.ToString(),
                        [Telemetry.ErrorType] = verdict.ErrorType,
                        [Telemetry.SecurityFinding] = verdict.Violation ?? ""
                    }));

                // The tool is recorded as withheld for the rest of the turn, beside those refused before it.
                if (session is not null)
                {
                    string[] quarantinedToolNames = [.. ReadQuarantinedTools(session, contextProvider), toolName];
                    await contextProvider.SetVariableAsync(session, Constants.ContextKeys.QuarantinedTools, quarantinedToolNames);
                }

                // The model reads the fact of the quarantine in place of the result and the session
                // keeps that fact, never the refused text.
                return await promptComposerService.ComposeQuarantinedResultAsync(toolName);
            })
            .Build();

        #region Utilities

        // Whether the source behind an external tool currently holds a completed approval of its offer.
        bool SourceIsAvailableAndSecure(string toolName)
            => mcpToolServers.TryGetValue(toolName, out UsesMCPServerAttribute? server)
                ? guardRailService.IsServerAvailableAndSecure(server)
                : partnerToolPeers.TryGetValue(toolName, out Records.PeerReference? peer) && guardRailService.IsPeerAvailableAndSecure(peer);

        #endregion
    }

    /// <summary>
    /// Composes the agent's two-layer instructions and closes them with the colleagues it holds.
    /// Which colleagues resolved is known only here, so the composer is handed the roster and never asked to discover one.
    /// </summary>
    /// <param name="agentPrompt">The agent's own domain prompt.</param>
    /// <param name="peerCapable">Whether this agent sits inside the A2A topology at either end.</param>
    /// <param name="peerTerritories">Function name → the colleague's own statement of what falls to it.</param>
    private async Task<string> ComposeInstructionsWithColleaguesAsync(
        Records.Prompt agentPrompt,
        bool peerCapable,
        IReadOnlyDictionary<string, string> peerTerritories)
    {
        // The framework layer over the domain layer, with the consultation policy where the agent can be asked.
        string instructions = await promptComposerService.ComposeAgentInstructionsAsync(agentPrompt, peerCapable);

        // Null for an agent that holds no colleague.
        string? colleagues = await promptComposerService.ComposeColleaguesDeclarationAsync(peerTerritories);

        // The roster stays ahead of the per-turn tail, in the cached prefix: it is fixed for the agent's life.
        return colleagues is null ? instructions : $"{instructions}\n{colleagues}\n";
    }

    /// <summary>
    /// Builds one callable function per colleague the agent declares with <c>[ConsultsAgent]</c>, leaving out those it cannot reach.
    /// The startup checks already refused a declaration naming no agent, so a colleague missing here is one whose endpoint did not answer.
    /// </summary>
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
        // The colleagues in the order the agent's author declared them.
        ConsultsAgentAttribute[] attributes = [.. agentType.GetCustomAttributes<ConsultsAgentAttribute>()];

        // Consulting nobody, the agent still reads the consultation policy in its instructions, since any colleague may consult it.
        if (attributes.Length == 0)
        {
            logger.LogDebug("Agent {AgentTypeName} consults no colleague", agentType.Name);
            return [];
        }

        // Switched off, an agent runs unaware it ever had colleagues: a deployment that cannot afford the extra turns pays none of them.
        if (!PeerConsultationEnabled)
        {
            logger.LogInformation("Peer consultation is disabled: agent {AgentTypeName} will not see its {Count} declared colleague(s)", agentType.Name, attributes.Length);
            return [];
        }

        // How many consultations one user turn may spend before the exchange is cut short.
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

            // In declaration order, which keeps the model's tool list identical from one run to the next.
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

        #region Utilities

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

            // A colleague that cannot be resolved is left out and reported, the agent running without it.
            if (resolvedPeer is not (AIAgent peerAgent, AgentCard peerCard))
            {
                logger.LogWarning("Agent {AgentTypeName} cannot reach declared colleague '{PeerIntent}'; it will run without it", agentType.Name, attribute.Intent);
                return null;
            }

            // A partner's card is text from outside that the model reads as instructions: its description is examined first.
            if (ToolGuardrailEnabled && peer.Instance is not null)
            {
                // The verdict on the description under the function name the model would call.
                Records.GuardRailResult cardVerdict = await guardRailService.AdmitPartnerCardAsync(peer, ToFunctionName(peer), peerCard.Description);

                // A refused partner is not offered: no function and no territory line.
                if (!cardVerdict.Compliant)
                {
                    logger.LogWarning(
                        "Agent {AgentTypeName} will run without colleague '{PeerIntent}': its partner's card was refused — {Violation}",
                        agentType.Name, attribute.Intent, cardVerdict.Violation);
                    return null;
                }
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
                    // The two rules a consultation is bound by, evaluated against the caller's live
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

            // The function name is what the model calls, so it is derived from the colleague's intent and its
            // partner rather than from the card's free-form name.
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

        #endregion
    }

    /// <summary>
    /// Applies the two rules a consultation is bound by and charges the round to the turn's budget when they pass.
    /// A refusal comes back as an ordinary answer, never an exception: the asking agent is mid-turn with a user waiting.
    /// </summary>
    /// <param name="callerIntent">Asking agent, named in the diagnostics.</param>
    /// <param name="peerIntent">Colleague being consulted, named in the diagnostics.</param>
    /// <param name="callerSession">The asking agent's session; null when it has none yet.</param>
    /// <param name="maxRoundsPerTurn">Consultations one user turn may spend before the exchange is cut short.</param>
    /// <param name="contextProvider">Context store of the asking agent, holding the turn's round count.</param>
    /// <returns>The serialized refusal envelope; <c>null</c> when the consultation may proceed.</returns>
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
        if (contextProvider.GetFrameworkKeyValue(callerSession, Constants.ContextKeys.ServingConsultation) is not null)
        {
            logger.LogWarning("Agent '{CallerIntent}' attempted to consult '{PeerIntent}' while itself answering a colleague", callerIntent, peerIntent);
            return RefusalEnvelope("You are currently answering a colleague and a colleague may not consult a further colleague. Answer with what you know.");
        }

        // The second rule: a cap on how many rounds one user turn may spend talking to colleagues.
        // It is a safety net and not the mechanism — convergence is asked of the prose, in the
        // PeerConsultation policy — so hitting it is a warning rather than a fault.
        int roundsSoFar = ReadConsultationRounds(callerSession, contextProvider);
        if (roundsSoFar >= maxRoundsPerTurn)
        {
            logger.LogWarning("Agent '{CallerIntent}' exhausted its {MaxRounds} consultation round(s) for this turn", callerIntent, maxRoundsPerTurn);
            return RefusalEnvelope($"This exchange has run for {roundsSoFar} rounds and must end now. Answer with what you already have.");
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
    /// Returns a copy of the run options declaring who is asking.
    /// The A2A layer carries the property as message metadata to the answering agent, so the requester is named outside the question's text.
    /// </summary>
    private static AgentRunOptions WithDeclaredCaller(AgentRunOptions? options, string callerIntent)
    {
        // A copy, so the options the pipeline handed in stay as they were for whoever else reads them.
        AgentRunOptions declaredOptions = options?.Clone() ?? new AgentRunOptions();

        // The asking agent's intent under the key the answering side reads it from.
        declaredOptions.AdditionalProperties ??= [];
        declaredOptions.AdditionalProperties[Constants.MessageProperties.CallerIntent] = callerIntent;

        // Handed to the colleague's run in place of the options the pipeline passed in.
        return declaredOptions;
    }

    /// <summary>
    /// Reads the asking agent's consultation counter for the current turn, tolerating the
    /// <see cref="JsonElement"/> form a value takes once its session has been persisted and reloaded.
    /// </summary>
    private static int ReadConsultationRounds(AgentSession callerSession, MorganaAIContextProvider contextProvider)
        => contextProvider.GetFrameworkKeyValue(callerSession, Constants.ContextKeys.ConsultationRounds) switch
        {
            int rounds => rounds,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetInt32(),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0
        };

    /// <summary>
    /// Reads the tools withheld in this turn, tolerating the <see cref="JsonElement"/> form a value
    /// takes once its session has been persisted and reloaded.
    /// </summary>
    private static string[] ReadQuarantinedTools(AgentSession session, MorganaAIContextProvider contextProvider)
        => contextProvider.GetFrameworkKeyValue(session, Constants.ContextKeys.QuarantinedTools) switch
        {
            string[] toolNames => toolNames,
            JsonElement { ValueKind: JsonValueKind.Array } element => [.. element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")],
            _ => []
        };

    /// <summary>
    /// Takes what a colleague reports having spent off its answer and charges it home when that colleague was published elsewhere.
    /// The figure is always taken off, since a model given a number narrates it, while one of this installation's agents already charged the conversation itself.
    /// </summary>
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
            // An envelope that does not parse is handed on untouched: the answer stands and only the charge is lost.
            logger.LogWarning(ex, "Could not read what '{PeerIntent}' at '{PeerSystem}' spent; its answer stands and the charge is lost", peerIntent, peerSystem ?? Constants.Morgana);
            return peerResponse;
        }
    }

    /// <summary>Renders a refusal in the envelope a colleague's real answer travels in.</summary>
    private static string RefusalEnvelope(string message)
        => JsonSerializer.Serialize(new Records.PeerConsultationResponse(message, false), Records.DefaultJsonSerializerOptions);

    /// <summary>
    /// Builds the name under which a colleague is offered as a callable function, the one the startup checks derive too.
    /// Two partners' names can fold to one function, which the startup checks refuse before a provider rejects the tool list.
    /// </summary>
    /// <param name="peer">Colleague being offered.</param>
    internal static string ToFunctionName(Records.PeerReference peer)
    {
        // A partner's colleague carries the partner in its name, so two colleagues handling one intent at two partners stay apart.
        string peerName = peer.Instance is null ? peer.Intent : $"{peer.Instance}_{peer.Intent}";

        // The prefix sets a colleague apart from the agent's own tools and anything outside a function name's alphabet folds to an underscore.
        return $"{Constants.AgentToAgent.PeerFunctionNamePrefix}{new string([.. peerName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')])}";
    }

    /// <summary>
    /// Names why an MCP tool may not join the agent; null when its name is free.
    /// A duplicate name would have the provider refuse the agent's whole tool list and a borrowed one would inherit an exemption granted by name.
    /// </summary>
    /// <param name="mcpToolName">Name the MCP server declares for the tool.</param>
    /// <param name="agentToolNames">The names of the agent's base tools and of its tool class's tools.</param>
    /// <param name="admittedMCPToolNames">The MCP tools already admitted for this agent.</param>
    /// <returns>The reason the name is refused; null when it is free.</returns>
    public static string? FindMCPToolNameConflict(
        string mcpToolName,
        IReadOnlyCollection<string> agentToolNames,
        IReadOnlyCollection<string> admittedMCPToolNames)
    {
        // First rule: a tool of Morgana or of the domain already holds the name.
        if (agentToolNames.Contains(mcpToolName, StringComparer.OrdinalIgnoreCase))
            return "takes the name of a tool of Morgana or of the agent's domain";

        // Second rule: the prefix is reserved for the colleagues' functions.
        if (mcpToolName.StartsWith(Constants.AgentToAgent.PeerFunctionNamePrefix, StringComparison.OrdinalIgnoreCase))
            return "takes the prefix Morgana reserves for colleagues";

        // Third rule: an MCP tool admitted earlier already holds the name.
        if (admittedMCPToolNames.Contains(mcpToolName, StringComparer.OrdinalIgnoreCase))
            return "takes the name of a tool another MCP source already gave the agent";

        // The name collides with nothing.
        return null;
    }

    /// <summary>Collects the tools of every MCP server the agent declares, refusing each one whose name is taken.</summary>
    /// <param name="agentType">Agent type to inspect for [UsesMCPServer] attributes</param>
    /// <param name="agentIntent">The intent the agent serves, tagged on the security event of a refused tool</param>
    /// <param name="agentToolNames">Names of the agent's base tools and of its tool class's tools, which an MCP tool may not take</param>
    /// <param name="mcpToolServers">Filled with the server each admitted MCP tool came from, for the go/no-go read before every call</param>
    /// <returns>Discovered tools as AIFunctions, empty if no servers declared</returns>
    private async Task<List<AIFunction>> RegisterMCPToolsAsync(Type agentType, string agentIntent, IReadOnlyCollection<string> agentToolNames, Dictionary<string, UsesMCPServerAttribute> mcpToolServers)
    {
        // The attribute is repeatable: an agent may draw tools from several servers, HTTP and stdio mixed.
        UsesMCPServerAttribute[] attributes = [.. agentType.GetCustomAttributes<UsesMCPServerAttribute>()];

        // Most agents use no MCP server, which is why this is a debug line and not a warning.
        if (attributes.Length == 0)
        {
            logger.LogDebug("Agent {AgentTypeName} does not use MCP servers", agentType.Name);
            return [];
        }

        logger.LogInformation("Agent {AgentTypeName} declares {AttributesLength} MCP server(s)", agentType.Name, attributes.Length);

        // The tools admitted so far, which a later tool's name may not take.
        List<AIFunction> mcpTools = [];

        // Each server is tried on its own, so a dead one costs its own tools and never the others' or the agent.
        foreach (UsesMCPServerAttribute attribute in attributes)
        {
            try
            {
                // The server's whole list, to be admitted tool by tool.
                IList<AIFunction> serverTools = await DiscoverMCPToolsFromServerAsync(attribute);

                // Each tool is admitted or refused on its own name.
                foreach (AIFunction serverTool in serverTools)
                {
                    // The reason this tool's name collides with a tool that entered before it: null when free.
                    string? conflictReason = FindMCPToolNameConflict(
                        serverTool.Name,
                        agentToolNames,
                        [.. mcpTools.Select(admittedTool => admittedTool.Name)]);

                    // A colliding tool is dropped alone: the rest of the server's list still serves the agent.
                    if (conflictReason is not null)
                    {
                        logger.LogWarning(
                            "MCP tool {McpToolName} from server {ServerCommand} refused for agent {AgentTypeName}: it {Reason}",
                            serverTool.Name, attribute.Command, agentType.Name, conflictReason);

                        // Counts the refusal without the tool's name: an MCP server chooses its names.
                        // No span event: an agent is created outside any turn.
                        Telemetry.SecurityEvents.Add(1,
                            new KeyValuePair<string, object?>(Telemetry.SecurityDoor, Telemetry.SecurityDoors.MCPAdmission),
                            new KeyValuePair<string, object?>(Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.Refused),
                            new KeyValuePair<string, object?>(Telemetry.AgentIntent, agentIntent));
                        continue;
                    }

                    // The name is free, so the tool joins the agent.
                    mcpTools.Add(serverTool);

                    // The tool's server is remembered so its availability is read before each call.
                    mcpToolServers[serverTool.Name] = attribute;
                }
            }
            catch (Exception ex)
            {
                // The server's tools are lost to this agent; the next server is still tried.
                logger.LogError(ex, "Failed to register MCP tools from server: {AttributeCommand}", attribute.Command);
            }
        }

        // They enter the agent's tool list after its own tools, whose names they were checked against.
        return mcpTools;
    }

    /// <summary>
    /// Lists one MCP server's tools: those the guard admitted when the tool guard is on, the registry's listing otherwise.
    /// They keep the server's names and schemas and serve the agent for its whole life, since it discovers them once, at creation.
    /// </summary>
    /// <param name="serverAttribute">The server's declaration: transport, command and arguments.</param>
    /// <returns>The server's tools.</returns>
    private async Task<IList<AIFunction>> DiscoverMCPToolsFromServerAsync(UsesMCPServerAttribute serverAttribute)
    {
        logger.LogInformation("Registering MCP tools from server: {ServerAttributeCommand}", serverAttribute.Command);

        // With the tool guard on, the server's offer is examined before any of its tools reaches the agent.
        IList<AIFunction> mcpTools = ToolGuardrailEnabled
            ? [.. (await guardRailService.AdmitMCPServerToolsAsync(serverAttribute)).Tools]
            : await imcpClientRegistryService.DiscoverResilientToolsAsync(serverAttribute);

        // A server that offers no tool, is refused or cannot be reached leaves the agent with its other tools.
        if (mcpTools.Count == 0)
        {
            logger.LogWarning("No tools discovered from MCP server: {ServerAttributeCommand}", serverAttribute.Command);
            return [];
        }

        foreach (AIFunction mcpTool in mcpTools)
            logger.LogInformation("Registered MCP tool: {McpToolName}", mcpTool.Name);

        logger.LogInformation("Successfully registered {McpToolsCount} MCP tools from {ServerAttributeCommand}", mcpTools.Count, serverAttribute.Command);

        // The server's tools as one list of their own, apart from the registry's.
        return [.. mcpTools];
    }
}