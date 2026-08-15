using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

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
        [InterviewPass.DomainMapper] = "DomainMapper",
        [InterviewPass.AgentModeler] = "AgentModeler",
        [InterviewPass.AgentVoicer] = "AgentVoicer",
        [InterviewPass.ToolkitModeler] = "ToolkitModeler",
        [InterviewPass.AgentInstructor] = "AgentInstructor",
        [InterviewPass.AgentFormatter] = "AgentFormatter"
    };

    /// <summary>
    /// What the agent is sent to open a pass. Never shown to the client: a chat completion needs a
    /// user turn to answer, not because anybody said it.
    /// </summary>
    private static readonly Dictionary<InterviewPass, string> BootstrapMessages = new()
    {
        [InterviewPass.DomainMapper] = "Begin the interview.",
        [InterviewPass.AgentModeler] = "Nothing of its agent is written yet. This step settles its Target, "
                                       + "which gives it everything it will be able to take on.",
        [InterviewPass.AgentVoicer] = "Its Target is settled. This step settles its Personality, which gives "
                                      + "it the voice it meets people with.",
        [InterviewPass.ToolkitModeler] = "Its Target and its Personality are settled. This step settles its "
                                         + "Toolkit, which gives it everything it can reach outside the conversation.",
        [InterviewPass.AgentInstructor] = "Its Target, its Personality and its Toolkit are settled. This step "
                                          + "settles its Instructions, which give it how it goes about the work.",
        [InterviewPass.AgentFormatter] = "Everything else about this agent is settled. This last step settles its "
                                         + "Formatting, which gives it how what it finds reaches the person reading it."
    };

    /// <summary>
    /// Loads <c>alembic.json</c> and composes the two-layer prompt each pass runs on.
    /// </summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>
    /// Splices <see cref="Morgana.AI.Records.ToolDescriptionContextGuidance"/> into a tool's
    /// description where it declares context-scoped parameters — needed here only to hand
    /// <see cref="MorganaToolAdapter"/> the same composer the rest of the framework uses.
    /// </summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Every check decidable by reading the Draft alone, consulted by <c>GetFindings</c> so a pass
    /// can see the same deterministic findings Review shows, filtered to its own business.
    /// </summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>
    /// Composes the prompt an authored agent's model will actually read, consulted by
    /// <c>GetComposedPrompt</c> so a pass can check its own prose before handing it over.
    /// </summary>
    private readonly IRecapService recapService;

    /// <summary>
    /// Resolves the chat client every pass runs on — always <see cref="Records.LLMTier.Performance"/>,
    /// never the tier a domain agent itself will run on.
    /// </summary>
    private readonly ILLMService llmService;

    /// <summary>
    /// The Draft under construction for this circuit, and the sitting <see cref="Keep"/> writes into
    /// it every turn so the save file is never behind what is on screen.
    /// </summary>
    private readonly IDraftStateService draftStateService;

    private readonly ILogger logger;

    /// <summary>
    /// The agent conducting the current pass. Rebuilt by <see cref="BuildAgentAsync"/> every time a
    /// pass is entered, since the toolset itself changes from one pass to the next.
    /// </summary>
    private AIAgent? agent;

    /// <summary>
    /// The session carrying <see cref="agent"/>'s history for the pass in progress. Never carried
    /// across a pass boundary — see the remarks on <see cref="EnterPassAsync"/> for why.
    /// </summary>
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

        // An interview brought back in a save file resumes where it stopped, and resumes the way a
        // step back does: the step is re-entered with a fresh agent and a fresh session reading what
        // is already written as settled fact. The conversation is not restored because it was never
        // the memory — the configuration is.
        if (draftStateService.Current?.Sitting is { } sitting)
        {
            Current.Map.AddRange(sitting.Map);
            Current.At = sitting.At;
            Current.Agent = sitting.Agent;

            await EnterPassAsync(Current, sitting.Pass, cancellationToken, revisiting: true);

            // The reopening turn writes its own example when it has one, and that fresh one wins.
            // Only when it stayed silent does the one standing at save time come back, so the box
            // a client saved with does not go empty just because the model saw no reason to repeat
            // itself.
            Current.Example ??= sitting.Example;

            return Current;
        }

        await EnterPassAsync(Current, InterviewPass.DomainMapper, cancellationToken);

        return Current;
    }

    /// <inheritdoc />
    public async Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default)
    {
        InterviewState interviewState = Current ?? await StartAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(answer))
            return interviewState;

        await ExchangeAsync(interviewState, answer, cancellationToken);

        // The seam belongs to the model. Once the state machine has CONFIRMED the pass settled —
        // SetPassCompleted is checked against Missing(), never believed — the next one opens in the
        // same turn, and the client simply gets the next question. Asking them to press a button
        // between two passes was asking them to acknowledge a boundary that is Alembic's alone: they
        // are having one interview, and the only thing they can say about a pass boundary is yes.
        //
        // A loop rather than an if, and it terminates: EnterPassAsync clears ReadyForReview, so it
        // takes a fresh confirmation from the new pass to go round again. A pass that genuinely
        // settles the moment it opens is legal — a toolkit the client has already described in full
        // — and would otherwise strand the interview one question short of moving.
        //
        // The Return pass is where it stops, because what follows is the client's: letting the agent
        // into the domain is the one decision of the interview that is theirs.
        while (interviewState is { ReadyForReview: true, Error: null } && interviewState.Pass != InterviewPass.AgentFormatter)
        {
            // Leaving the map is also stepping onto its first entry. The mapping pass settles no
            // agent, so there would be nothing for the functional pass to be about otherwise.
            if (interviewState.Pass == InterviewPass.DomainMapper)
                interviewState.At = 0;

            await EnterPassAsync(interviewState, interviewState.Pass + 1, cancellationToken);
        }

        return interviewState;
    }

    /// <inheritdoc />
    public async Task<bool> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { } interviewState)
            return false;

        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // Every domain has the fallback, including one being written from nothing this minute. It is
        // the single intent no interview authors and no client edits: the classifier goes there when
        // it cannot place a message, and a domain without it has nowhere to put what it does not
        // cover.
        draft.EnsureFallbackIntent();

        interviewState.Intent.Origin = Provenance.Authored;
        interviewState.Agent.Origin = Provenance.Authored;
        interviewState.Agent.ID = interviewState.Intent.Name;
        interviewState.Agent.Code.Inferred = true;
        interviewState.Agent.Code.AgentClassName = ProposeClassName(interviewState.Intent.Name, "Agent");

        // An agent with no native tools gets no tool class, and that is a legal shape rather than a
        // gap: an MCP-only agent's tools arrive at runtime and never appear in agents.json.
        interviewState.Agent.Code.ToolClassName = interviewState.Agent.Tools.Count > 0
            ? ProposeClassName(interviewState.Intent.Name, "Tool")
            : null;

        draft.Intents.Add(interviewState.Intent);
        draft.Agents.Add(interviewState.Agent);

        // The map is spent the moment this was the last of it, and an interview nobody is having is
        // not something to resume into.
        draft.Sitting = interviewState.At + 1 < interviewState.Map.Count
            ? new InterviewSitting
            {
                Pass = InterviewPass.AgentModeler,
                At = interviewState.At + 1,
                Map = [.. interviewState.Map],
                Agent = new AgentDraft()
            }
            : null;

        // Re-setting the same instance is what raises Changed, so a page showing the Draft
        // re-renders whether or not the Draft object itself was new.
        draftStateService.Set(draft);

        // Down the map, not out of the interview. The map is the promise the client made when they
        // said what their business gets asked, and an interview that stopped at the first agent
        // would leave them to remember the rest of their own list.
        if (interviewState.At + 1 < interviewState.Map.Count)
        {
            interviewState.At++;
            interviewState.Agent = new AgentDraft();

            await EnterPassAsync(interviewState, InterviewPass.AgentModeler, cancellationToken);
            return true;
        }

        Abandon();
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> BackAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { } interviewState)
            return false;

        // A finished agent steps back into its own last step rather than the one before it: what the
        // client is looking at is the whole of what was written, and what they want is to change a
        // line of it.
        if (interviewState is { Pass: InterviewPass.AgentFormatter, ReadyForReview: true })
        {
            await EnterPassAsync(interviewState, InterviewPass.AgentFormatter, cancellationToken, revisiting: true);
            return true;
        }

        switch (interviewState.Pass)
        {
            // The map is the first thing there is. Behind it is the landing, which is not a step.
            case InterviewPass.DomainMapper:
                return false;

            // Out of the first entry is back onto the map itself. The entries survive — they are the
            // map, not this pass's working copy — so the mapper reads its own list back and the
            // client changes what they came to change.
            case InterviewPass.AgentModeler when interviewState.At == 0:
                interviewState.At = -1;
                await EnterPassAsync(interviewState, InterviewPass.DomainMapper, cancellationToken, revisiting: true);
                return true;

            // Out of an entry into the one before it, which is in the domain already. It comes back
            // out of the Draft and into the client's hands: an agent being revised must not also be
            // sitting in the configuration, or committing it a second time would write it twice.
            case InterviewPass.AgentModeler:
                interviewState.At--;
                Uncommit(interviewState);
                await EnterPassAsync(interviewState, InterviewPass.AgentFormatter, cancellationToken, revisiting: true);
                return true;

            default:
                await EnterPassAsync(interviewState, interviewState.Pass - 1, cancellationToken, revisiting: true);
                return true;
        }
    }

    /// <summary>
    /// What Alembic actually asked the client this turn: the last question, not everything it wrote.
    /// </summary>
    /// <remarks>
    /// A run can hold several assistant messages — the model writes its question, calls a tool, and
    /// writes again — and <c>AgentResponse.Text</c> is all of them joined, so a question repeated
    /// after a tool call reached the screen twice over. The last message is not the answer either:
    /// the tail of a run is often the model narrating its own work ("I'll wait for their answer
    /// before setting the Target"), which leaves the client facing a box under a sentence that asks
    /// them nothing while the real question scrolls out of existence.
    ///
    /// So what is shown is the last message that actually asks something. The test is the question
    /// mark, which is the same law the interviewer is held to in prose — one sentence, one question
    /// mark — and this is the screen that law exists for. The fallback is still the last thing said:
    /// a turn that asks nothing at all is a defect, and showing it is how anyone finds out.
    /// </remarks>
    private static string LastAsked(AgentResponse response)
    {
        string[] said = [.. response.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)];

        string? asked = said.LastOrDefault(t => t.Contains('?'));

        return asked ?? said.LastOrDefault() ?? response.Text?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Writes where the interview stands into the Draft, so the save file is never out of date.
    /// </summary>
    /// <remarks>
    /// Every turn, rather than on a button. A "save now" the client has to remember is a save they
    /// remember after the tab is gone; keeping the Draft current at every exchange makes the download
    /// beside the question true whenever it is taken, which is the only version of this that helps
    /// somebody who closed a window by accident.
    /// </remarks>
    private void Keep(InterviewState interviewState)
    {
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        draft.Sitting = new InterviewSitting
        {
            Pass = interviewState.Pass,
            At = interviewState.At,
            Map = [.. interviewState.Map],
            Agent = interviewState.Agent,
            Example = interviewState.Example
        };

        draftStateService.Set(draft);
    }

    /// <summary>
    /// Takes the entry the interview has just stepped back onto out of the Draft and back into hand.
    /// </summary>
    private void Uncommit(InterviewState interviewState)
    {
        if (draftStateService.Current is not { } draft)
            return;

        AgentDraft? committed = draft.Agents.FirstOrDefault(a =>
            string.Equals(a.ID, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase));

        if (committed is not null)
        {
            draft.Agents.Remove(committed);
            interviewState.Agent = committed;
        }

        draft.Intents.RemoveAll(i =>
            string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase));

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
    private async Task EnterPassAsync(
        InterviewState interviewState,
        InterviewPass interviewPass,
        CancellationToken cancellationToken,
        bool revisiting = false)
    {
        interviewState.Pass = interviewPass;
        interviewState.ReadyForReview = false;
        interviewState.PassOpenedAt = interviewState.Exchanges;

        await BuildAgentAsync(interviewState, PassPromptIds[interviewPass]);

        // The opening question of the whole interview is nobody's to phrase but the client's own:
        // there is nothing yet for a model to be reading back, so a model asked for it is either
        // repeating the one sentence that fits or drifting off it. Fixed rather than composed —
        // and the session still needs building above, since the client's first answer lands on it.
        if (interviewPass == InterviewPass.DomainMapper && !revisiting)
        {
            interviewState.Question = OpeningQuestion;
            interviewState.Choice = null;
            interviewState.Example = null;
            interviewState.Traits = [];
            interviewState.Chosen = false;
            interviewState.Exchanges++;

            // ExchangeAsync always ends in Keep, precisely so the Draft behind Save my work is true
            // the moment a question is on screen — including the very first one, before the client
            // has said anything. Skipping the model here must not also skip that.
            Keep(interviewState);
            return;
        }

        await ExchangeAsync(interviewState, Bootstrap(interviewState, interviewPass, revisiting), cancellationToken);
    }

    /// <summary>
    /// The one question every interview opens on, fixed rather than composed: the client has said
    /// nothing yet, so there is nothing for a model to phrase differently from one run to the next.
    /// </summary>
    private const string OpeningQuestion =
        "Morgana takes on a handful of your own processes, one entry each becoming its own agent — "
        + "what would you like her to handle for you?";

    /// <summary>
    /// What the agent is sent to open a pass.
    /// </summary>
    /// <remarks>
    /// Every pass past the map runs once per entry, and where it is standing is a fact the state
    /// machine holds: which entry of how many, which intent, what is already written on that agent
    /// and what this step is about to give it. All of it goes in the opening message, because a pass
    /// that has to work it out either spends a tool call on it or guesses — and the client is owed a
    /// first sentence that names their agent and says what this stage is for, which is not something
    /// to be reconstructed from context.
    /// </remarks>
    private static string Bootstrap(InterviewState interviewState, InterviewPass interviewPass, bool revisiting)
    {
        string opening = interviewPass == InterviewPass.DomainMapper
            ? BootstrapMessages[interviewPass]
            : $"The map is settled and the client is still here. You are on entry {interviewState.At + 1} "
              + $"of {interviewState.Map.Count}: the intent '{interviewState.Intent.Name}', which the map "
              + $"describes as: {interviewState.Intent.Description}. "
              + BootstrapMessages[interviewPass];

        // Arriving on a step the client chose to come back to is not the same as arriving on a step
        // that has just opened, and the difference is what the first question should be. Everything
        // this step settled is still written, so opening it as if nothing were there would ask them
        // to dictate their own answers a second time — which is the circling this whole interview is
        // built to avoid, only this time Alembic starts it.
        return revisiting
            ? "The client has stepped back to this part of the interview to change something already "
              + "settled. Read what is there before you say anything, then ask in one sentence what "
              + "they want different — never open it again from nothing. " + opening
            : opening;
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
    private async Task BuildAgentAsync(InterviewState interviewState, string interviewerId)
    {
        Records.Prompt interviewer = alembicPromptService.Resolve(interviewerId);

        List<Records.ToolDefinition> definitions =
            interviewer.GetAdditionalPropertyOrDefault<List<Records.ToolDefinition>>("Tools", []);

        InterviewTools tools = new InterviewTools(interviewState, draftStateService, draftValidationService, recapService);
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter(promptComposerService);

        // The delegate map is the one place a tool's name, its declaration and its implementation
        // meet. AddTool validates the pair — parameter count, names, required/optional — and throws
        // on a mismatch, so a declaration that has drifted from its method fails here rather than
        // reaching the model as a schema nothing can satisfy.
        Dictionary<string, Delegate> implementations = new(StringComparer.Ordinal)
        {
            [nameof(InterviewTools.DeclareIntent)] = tools.DeclareIntent,
            [nameof(InterviewTools.DropIntent)] = tools.DropIntent,
            [nameof(InterviewTools.GetDomainMap)] = tools.GetDomainMap,
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
            [nameof(InterviewTools.SetChoice)] = tools.SetChoice,
            [nameof(InterviewTools.SetExample)] = tools.SetExample,
            [nameof(InterviewTools.SetTraits)] = tools.SetTraits,
            [nameof(InterviewTools.GetExistingIntents)] = tools.GetExistingIntents,
            [nameof(InterviewTools.GetComposedPrompt)] = tools.GetComposedPrompt,
            [nameof(InterviewTools.GetFindings)] = tools.GetFindings,
            [nameof(InterviewTools.SetPassCompleted)] = tools.SetPassCompleted
        };

        foreach (Records.ToolDefinition definition in definitions)
        {
            if (!implementations.TryGetValue(definition.Name, out Delegate? implementation))
                throw new InvalidOperationException(
                    $"alembic.json declares tool '{definition.Name}' for '{interviewerId}', but InterviewTools has no method by that name.");

            toolAdapter.AddTool(definition.Name, implementation, definition);
        }

        // Performance, resolved directly rather than through CompleteWithSystemPromptAsync, which
        // always runs on the cheapest configured tier. Writing non-contradictory dispositive prose
        // is the exact task the Efficiency die is weakest at.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = $"alembic-{interviewerId.ToLowerInvariant()}",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                Instructions = await alembicPromptService.ComposeAsync(interviewerId),
                Tools = [.. await toolAdapter.CreateAllFunctionsAsync()]
            }
        });

        session = await agent.CreateSessionAsync();
    }

    /// <summary>
    /// One round trip: the client's words in, Alembic's words out, and whatever its tools wrote
    /// along the way already in the interviewState.
    /// </summary>
    private async Task ExchangeAsync(InterviewState interviewState, string message, CancellationToken cancellationToken)
    {
        if (agent is null || session is null)
            return;

        interviewState.Error = null;
        interviewState.PendingChoice = null;
        interviewState.PendingExample = null;
        interviewState.PendingTraits.Clear();
        interviewState.Changed.Clear();

        // A before/after diff of every field Snapshot exposes, rather than trusting the tools
        // themselves to report what they touched: a tool answers the model, it does not report to
        // the UI, so this is the one place that knows which panel rows actually changed this turn.
        Dictionary<string, string?> before = interviewState.Snapshot();

        try
        {
            AgentResponse response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, message), session, cancellationToken: cancellationToken);

            foreach ((string field, string? value) in interviewState.Snapshot())
                if (!string.Equals(value, before[field], StringComparison.Ordinal))
                    interviewState.Changed.Add(field);

            string asked = LastAsked(response);

            // A turn that spent itself entirely on tool calls is legitimate — reading findings or a
            // composed prompt takes a round trip that has nothing to say to the client — and the
            // question already on the screen simply stands. Only the question text is gated on this:
            // Choice, Example and Traits are promoted from their Pending* counterparts regardless,
            // because a turn CAN legitimately call SetChoice (or SetExample, SetTraits) without also
            // producing new question text — e.g. a validation round that decides the question already
            // on screen now deserves a shortcut button — and gating all four on the same condition
            // silently dropped a button the model genuinely offered, with no way for it to reappear on
            // a later turn either, since PendingChoice is reset to null at the top of every exchange.
            if (asked.Length > 0)
            {
                interviewState.Question = asked;
                interviewState.Exchanges++;
            }

            interviewState.Choice = interviewState.PendingChoice;
            interviewState.Example = interviewState.PendingExample;
            interviewState.Traits = [.. interviewState.PendingTraits];
            interviewState.Chosen = false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The interview turn failed");
            interviewState.Error = $"The turn could not be completed: {ex.Message}";
        }

        // Here, rather than after an answer, because the turn that OPENS a pass is a turn like any
        // other and the file behind Save my work has to be true the moment it is on the screen —
        // including on the very first question, before the client has said anything, which is the
        // one point at which the Draft did not exist yet and the download handed back an empty one.
        // Outside the catch too: an interview that has just failed a turn is precisely one somebody
        // wants to take away.
        Keep(interviewState);
    }

    /// <summary>
    /// Proposes a class name from an intent name, by the framework's own naming convention.
    /// </summary>
    private static string? ProposeClassName(string? intentName, string suffix) =>
        string.IsNullOrWhiteSpace(intentName)
            ? null
            : $"{char.ToUpperInvariant(intentName[0])}{intentName[1..]}{suffix}";
}
