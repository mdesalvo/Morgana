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
    /// The pass conducted when an interview begins.
    /// </summary>
    private const string FunctionalPassPromptId = "FunctionalPass";

    /// <summary>
    /// What the agent is sent when the transcript is still empty. Never shown to the client: a
    /// chat completion needs a user turn to answer, not because anybody said it.
    /// </summary>
    private const string BootstrapMessage = "Begin the interview.";

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

        await BuildAgentAsync(Current, FunctionalPassPromptId);
        await ExchangeAsync(Current, BootstrapMessage, cancellationToken);

        return Current;
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
        state.Agent.Code.AgentClassName = ProposeClassName(state.Intent.Name);

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

        try
        {
            AgentResponse response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, message), session, cancellationToken: cancellationToken);

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
    private static string? ProposeClassName(string? intentName) =>
        string.IsNullOrWhiteSpace(intentName)
            ? null
            : $"{char.ToUpperInvariant(intentName[0])}{intentName[1..]}Agent";
}
