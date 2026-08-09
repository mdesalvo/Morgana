using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IInterviewService"/>: a Microsoft.Agents.AI agent, built with the framework's
/// own machinery, whose tools write into a C# state machine.
/// </summary>
/// <remarks>
/// <para>
/// Alembic is an agent of Morgana, so it is assembled the way one is — <c>MorganaToolAdapter</c>
/// binds each tool's declaration in <c>alembic.json</c> to its delegate and materialises the
/// <c>AIFunction</c>s, and <c>IChatClient.AsAIAgent</c> makes the agent. Not <c>MorganaAgent</c>
/// via <c>MorganaAgentAdapter</c>, which belongs to the routed world of <c>agents.json</c>,
/// <c>[HandlesIntent]</c>, base tools and per-conversation persistence: Alembic has none of that.
/// The reuse stops exactly where the resemblance does.
/// </para>
/// <para>
/// Tools rather than a structured reply, and the difference is not stylistic. Alembic now simply
/// <em>talks</em> to the client, and carries the configuration out of band — so the reply text and
/// the proposal stop being welded into one object, a malformed answer stops costing the client a
/// turn of their own interview, and a tool can answer back when a section arrives the wrong shape.
/// </para>
/// </remarks>
public class InterviewService : IInterviewService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> conducting each pass.
    /// </summary>
    private static readonly Dictionary<InterviewPass, string> PassPromptIds = new()
    {
        [InterviewPass.Functional] = "FunctionalPass",
        [InterviewPass.Toolkit] = "ToolkitPass",
        [InterviewPass.Return] = "ReturnPass"
    };

    /// <summary>
    /// What the agent is sent to open a pass. Never shown to the client: a chat completion needs a
    /// user turn to answer, not because anybody said it.
    /// </summary>
    private static readonly Dictionary<InterviewPass, string> BootstrapMessages = new()
    {
        [InterviewPass.Functional] = "Begin the interview.",
        [InterviewPass.Toolkit] = "The client is still here and the previous pass is settled. Begin this one.",
        [InterviewPass.Return] = "The client is still here and the toolkit is settled. Begin the last pass."
    };

    private readonly IAlembicPromptService alembicPromptService;
    private readonly IPromptComposerService promptComposerService;
    private readonly IDraftValidationService draftValidationService;
    private readonly IRecapService recapService;
    private readonly ILLMService llmService;
    private readonly IDraftStateService draftStateService;
    private readonly ILogger logger;

    /// <summary>
    /// The agent conducting the current pass, and the session carrying its history.
    /// </summary>
    private AIAgent? agent;
    private AgentSession? session;

    /// <inheritdoc />
    public InterviewState? Current { get; private set; }

    /// <summary>
    /// Initializes the interview service.
    /// </summary>
    public InterviewService(
        IAlembicPromptService alembicPromptService,
        IPromptComposerService promptComposerService,
        IDraftValidationService draftValidationService,
        IRecapService recapService,
        ILLMService llmService,
        IDraftStateService draftStateService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.promptComposerService = promptComposerService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
        this.llmService = llmService;
        this.draftStateService = draftStateService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<InterviewState> StartAsync(CancellationToken cancellationToken = default)
    {
        Current = new InterviewState();

        await EnterPassAsync(Current, InterviewPass.Functional, cancellationToken);

        return Current;
    }

    /// <inheritdoc />
    public async Task<InterviewState> AdvanceAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { ReadyForReview: true } state || state.Pass == InterviewPass.Return)
            return Current ?? await StartAsync(cancellationToken);

        await EnterPassAsync(state, state.Pass + 1, cancellationToken);

        return state;
    }

    /// <inheritdoc />
    public async Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default)
    {
        InterviewState state = Current ?? await StartAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(answer))
            return state;

        state.Transcript.Add(new InterviewTurn(InterviewSpeaker.Client, answer));
        await ExchangeAsync(state, answer, cancellationToken);

        return state;
    }

    /// <inheritdoc />
    public void Commit()
    {
        if (Current is not { } state)
            return;

        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        state.Intent.Origin = Provenance.Authored;
        state.Agent.Origin = Provenance.Authored;
        state.Agent.ID = state.Intent.Name;
        state.Agent.Code.Inferred = true;
        state.Agent.Code.AgentClassName = ProposeClassName(state.Intent.Name, "Agent");

        // An agent with no native tools gets no tool class, and that is a legal shape rather than a
        // gap: an MCP-only agent's tools arrive at runtime and never appear in agents.json.
        state.Agent.Code.ToolClassName = state.Agent.Tools.Count > 0
            ? ProposeClassName(state.Intent.Name, "Tool")
            : null;

        draft.Intents.Add(state.Intent);
        draft.Agents.Add(state.Agent);

        Abandon();

        // Re-setting the same instance is what raises Changed, so a page showing the Draft
        // re-renders whether or not the Draft object itself was new.
        draftStateService.Set(draft);
    }

    /// <inheritdoc />
    public void Abandon()
    {
        Current = null;
        agent = null;
        session = null;
    }

    /// <summary>
    /// Moves the interview into a pass: new agent, new session, and the pass's opening question.
    /// </summary>
    /// <remarks>
    /// The session does not carry over, and that is the design rather than a limitation. A toolkit
    /// pass holding the entire functional interview in its context spends it re-litigating decisions
    /// already taken, and every turn of it thereafter. What must survive a pass boundary is the
    /// configuration, which is what <c>GetAgentSoFar</c> and <c>GetToolkit</c> hand back — read as
    /// settled fact rather than replayed as a conversation. The client's transcript is untouched:
    /// they are having one interview, and only the model starts again.
    /// </remarks>
    private async Task EnterPassAsync(InterviewState state, InterviewPass pass, CancellationToken cancellationToken)
    {
        state.Pass = pass;
        state.ReadyForReview = false;

        await BuildAgentAsync(state, PassPromptIds[pass]);
        await ExchangeAsync(state, BootstrapMessages[pass], cancellationToken);
    }

    /// <summary>
    /// Assembles the agent for one pass: its prompt, and only the tools that pass is allowed.
    /// </summary>
    /// <remarks>
    /// The toolset comes from the pass's own <c>Tools</c> declaration in <c>alembic.json</c>, so
    /// what a pass may write is settled by which tools exist rather than by a sentence asking it to
    /// abstain. The functional pass has no tool for an agent's instructions or formatting, and that
    /// is the whole of the constraint.
    /// </remarks>
    private async Task BuildAgentAsync(InterviewState state, string passId)
    {
        Records.Prompt pass = alembicPromptService.Resolve(passId);

        List<Records.ToolDefinition> definitions =
            pass.GetAdditionalPropertyOrDefault<List<Records.ToolDefinition>>("Tools", []);

        InterviewTools tools = new InterviewTools(state, draftStateService, draftValidationService, recapService);
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter(promptComposerService);

        // The delegate map is the one place a tool's name, its declaration and its implementation
        // meet. AddTool validates the pair — parameter count, names, required/optional — and throws
        // on a mismatch, so a declaration that has drifted from its method fails here rather than
        // reaching the model as a schema nothing can satisfy.
        Dictionary<string, Delegate> implementations = new(StringComparer.Ordinal)
        {
            [nameof(InterviewTools.SetIntent)] = tools.SetIntent,
            [nameof(InterviewTools.SetAgentTarget)] = tools.SetAgentTarget,
            [nameof(InterviewTools.SetAgentPersonality)] = tools.SetAgentPersonality,
            [nameof(InterviewTools.SetAgentInstructions)] = tools.SetAgentInstructions,
            [nameof(InterviewTools.SetAgentFormatting)] = tools.SetAgentFormatting,
            [nameof(InterviewTools.DeclareTool)] = tools.DeclareTool,
            [nameof(InterviewTools.SetToolParameter)] = tools.SetToolParameter,
            [nameof(InterviewTools.DropToolParameter)] = tools.DropToolParameter,
            [nameof(InterviewTools.DropTool)] = tools.DropTool,
            [nameof(InterviewTools.GetToolkit)] = tools.GetToolkit,
            [nameof(InterviewTools.GetAgentSoFar)] = tools.GetAgentSoFar,
            [nameof(InterviewTools.SetChoices)] = tools.SetChoices,
            [nameof(InterviewTools.GetExistingIntents)] = tools.GetExistingIntents,
            [nameof(InterviewTools.GetComposedPrompt)] = tools.GetComposedPrompt,
            [nameof(InterviewTools.GetFindings)] = tools.GetFindings,
            [nameof(InterviewTools.SetPassCompleted)] = tools.SetPassCompleted
        };

        foreach (Records.ToolDefinition definition in definitions)
        {
            if (!implementations.TryGetValue(definition.Name, out Delegate? implementation))
                throw new InvalidOperationException(
                    $"alembic.json declares tool '{definition.Name}' for pass '{passId}', but InterviewTools has no method by that name.");

            toolAdapter.AddTool(definition.Name, implementation, definition);
        }

        // Performance, resolved directly rather than through CompleteWithSystemPromptAsync, which
        // always runs on the cheapest configured tier. Writing non-contradictory dispositive prose
        // is the exact task the Efficiency die is weakest at.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = $"alembic-{passId.ToLowerInvariant()}",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                Instructions = await alembicPromptService.ComposeAsync(passId),
                Tools = [.. await toolAdapter.CreateAllFunctionsAsync()]
            }
        });

        session = await agent.CreateSessionAsync();
    }

    /// <summary>
    /// One round trip: the client's words in, Alembic's words out, and whatever its tools wrote
    /// along the way already in the state.
    /// </summary>
    private async Task ExchangeAsync(InterviewState state, string message, CancellationToken cancellationToken)
    {
        if (agent is null || session is null)
            return;

        state.Error = null;
        state.PendingChoices.Clear();
        state.Changed.Clear();

        Dictionary<string, string?> before = state.Snapshot();

        try
        {
            AgentResponse response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, message), session, cancellationToken: cancellationToken);

            foreach ((string field, string? value) in state.Snapshot())
                if (!string.Equals(value, before[field], StringComparison.Ordinal))
                    state.Changed.Add(field);

            string said = response.Text?.Trim() ?? string.Empty;

            // A turn that spent itself entirely on tool calls is legitimate — reading findings or a
            // composed prompt takes a round trip that has nothing to say to the client — but it
            // must not surface as an empty bubble.
            if (said.Length > 0)
                state.Transcript.Add(new InterviewTurn(
                    InterviewSpeaker.Alembic, said, [.. state.PendingChoices]));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The interview turn failed");
            state.Error = $"The turn could not be completed: {ex.Message}";
        }
    }

    /// <summary>
    /// Proposes a class name from an intent name, by the framework's own naming convention.
    /// </summary>
    private static string? ProposeClassName(string? intentName, string suffix) =>
        string.IsNullOrWhiteSpace(intentName)
            ? null
            : $"{char.ToUpperInvariant(intentName[0])}{intentName[1..]}{suffix}";
}
