using System.Diagnostics;
using Alembic.Interfaces;
using Alembic.Models;
using Alembic.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IInterviewService"/>: a Microsoft.Agents.AI agent, built with the framework's own machinery, whose tools write into a C# state machine.
/// <c>MorganaToolAdapter</c> and <c>AsAIAgent</c> build it as the framework builds an agent, never <c>MorganaAgentAdapter</c>, whose routed world of intents,
/// base tools and per-conversation persistence Alembic has none of. Tools carry the configuration beside a reply that simply talks, so a malformed answer
/// costs no turn of the client's and a tool answers back when a section arrives the wrong shape.
/// </summary>
public class InterviewService : IInterviewService
{
    /// <summary>The prompt in <c>alembic.json</c> conducting each pass.</summary>
    private static readonly Dictionary<InterviewStep, string> PassPromptIds = new()
    {
        [InterviewStep.DomainMapper] = "DomainMapper",
        [InterviewStep.AgentTarget] = "AgentTarget",
        [InterviewStep.AgentPersonality] = "AgentPersonality",
        [InterviewStep.AgentToolkit] = "AgentToolkit",
        [InterviewStep.AgentTerritory] = "AgentTerritory",
        [InterviewStep.AgentInstructions] = "AgentInstructions",
        [InterviewStep.AgentFormatting] = "AgentFormatting"
    };

    /// <summary>
    /// What the agent is sent to open a pass, never shown to the client.
    /// A chat completion needs a user turn to answer, though nobody said it.
    /// </summary>
    private static readonly Dictionary<InterviewStep, string> BootstrapMessages = new()
    {
        [InterviewStep.DomainMapper] = "Begin the interview.",
        [InterviewStep.AgentTarget] = "This step settles its Target, which gives it everything it will be "
                                       + "able to take on.",
        [InterviewStep.AgentPersonality] = "This step settles its Personality, which gives it the voice it meets "
                                      + "people with.",
        [InterviewStep.AgentToolkit] = "This step settles its Toolkit, which gives it everything it can "
                                         + "reach outside the conversation.",
        [InterviewStep.AgentTerritory] = "This step settles its Territory, which gives it what this agent is "
                                         + "the one to be asked about.",
        [InterviewStep.AgentInstructions] = "This step settles its Instructions, which give it how it goes "
                                          + "about the work.",
        [InterviewStep.AgentFormatting] = "This last step settles its Formatting, which gives it how what it "
                                         + "finds reaches the person reading it."
    };

    /// <summary>Loads <c>alembic.json</c> and composes the two-layer prompt each pass runs on.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>The framework's composer, handed to <see cref="MorganaToolAdapter"/> so the passes' tools are described as the framework describes any tool.</summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>The deterministic checks, which <c>GetFindings</c> shows a pass filtered to its own business, as Review shows them whole.</summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>Composes the prompt an authored agent's model will read, which <c>GetComposedPrompt</c> shows a pass before it hands its prose over.</summary>
    private readonly IRecapService recapService;

    /// <summary>Supplies the chat client every pass runs on, always Efficiency, never the tier the authored agent will run on.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// The circuit's Draft, into which <see cref="Keep"/> writes the sitting every turn.
    /// The save file is therefore never behind what is on screen.
    /// </summary>
    private readonly IDraftStateService draftStateService;

    /// <summary>Records every pass entered, every tool called and every turn that failed or timed out.</summary>
    private readonly ILogger logger;

    /// <summary>The agent conducting the current pass, rebuilt on entering each pass since the tools change from one pass to the next.</summary>
    private AIAgent? agent;

    /// <summary>The session carrying the current pass's history, never carried across a pass boundary.</summary>
    private AgentSession? session;

    /// <inheritdoc />
    public InterviewState? Current { get; private set; }

    /// <summary>
    /// How long one turn of the interview may take before it is given up on.
    /// A provider that stalls reports nothing, so without it the client would wait on a turn that never completes; it is generous, since what it ends is the wait with no end.
    /// </summary>
    private readonly TimeSpan turnCeiling;

    /// <summary>
    /// Whether a turn of this interview is running: 1 while one does, 0 otherwise.
    /// A disabled button arrives one render late and a second press would open a second turn over the same state and session, so it is refused rather than queued.
    /// </summary>
    private int running;

    /// <summary>
    /// Claims the interview for one turn; otherwise reports that somebody else holds it.
    /// Every public operation claims it first and gives it back however it ends: pressing twice is the same instruction arriving again, never a second one.
    /// </summary>
    private bool EnterTurn()
    {
        // The first gesture takes the turn; the rest find it taken.
        if (Interlocked.CompareExchange(ref running, 1, 0) == 0)
            return true;

        logger.LogWarning("A second gesture arrived while a turn was still running and was ignored");
        return false;
    }

    /// <summary>Gives the interview back, whatever the turn did.</summary>
    private void LeaveTurn() => Interlocked.Exchange(ref running, 0);

    /// <summary>Binds the interview to its prompts, the checks, the model and the circuit's Draft.</summary>
    public InterviewService(
        IAlembicPromptService alembicPromptService,
        IPromptComposerService promptComposerService,
        IDraftValidationService draftValidationService,
        IRecapService recapService,
        ILanguageModelService llmService,
        IDraftStateService draftStateService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.promptComposerService = promptComposerService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
        this.llmService = llmService;
        this.draftStateService = draftStateService;
        this.logger = logger;

        // The deployment may lengthen the wait for a slower provider; three minutes otherwise.
        turnCeiling = TimeSpan.FromSeconds(configuration.GetValue("Alembic:Work:TurnSeconds", 180));
    }

    /// <summary>Begins the interview (or resumes the sitting the Draft carries) and asks the opening question.</summary>
    public async Task<InterviewState> StartAsync(CancellationToken cancellationToken = default)
    {
        // A gesture arriving while a turn runs sees the interview as it stands.
        if (!EnterTurn())
            return Current ?? new InterviewState();

        try
        {
            return await StartCoreAsync(cancellationToken);
        }
        finally
        {
            LeaveTurn();
        }
    }

    /// <summary>
    /// Opens the interview, with the turn already claimed by whoever called.
    /// Answering when nothing is open starts it too, one gesture rather than two, so the claim is never taken twice on the way through.
    /// </summary>
    private async Task<InterviewState> StartCoreAsync(CancellationToken cancellationToken)
    {
        // A fresh state, filled from the save file when there is one.
        Current = new InterviewState();

        // An interview brought back in a save file resumes where it stopped and resumes the way a
        // step back does: the step is re-entered with a fresh agent and a fresh session reading what
        // is already written as settled fact. The conversation is not restored because it was never
        // the memory — the configuration is.
        if (draftStateService.Current?.Sitting is { } sitting)
        {
            Current.Map.AddRange(sitting.Map);
            Current.At = sitting.At;
            Current.Agent = sitting.Agent;

            // An edit saved half-done comes back an edit. Without this the agent in hand would be let
            // into the domain as freshly authored, at the end of the list, over C# facts it arrived
            // with — the three things the revision exists to get right.
            Current.Revision = sitting.Revision;

            // A correction saved half-done comes back with the interview it set aside.
            if (sitting.Resume is { } frontier)
                Current.Resume = StateOf(frontier);

            // The saved step reopened as a revisit, so the model reads what is written as settled.
            await EnterPassAsync(Current, sitting.Pass, cancellationToken, revisiting: true);

            // The reopening turn writes its own example when it has one and that fresh one wins.
            // Only when it stayed silent does the one standing at save time come back, so the box
            // a client saved with does not go empty just because the model saw no reason to repeat
            // itself.
            Current.Example ??= sitting.Example;

            return Current;
        }

        // A new interview always opens on the map: a domain is edited as a whole before any agent.
        await EnterPassAsync(Current, InterviewStep.DomainMapper, cancellationToken);

        return Current;
    }

    /// <summary>Opens an agent already in the domain at its Target, so the client can have it corrected.</summary>
    public async Task<bool> ReviseAsync(
        string agentID,
        CancellationToken cancellationToken = default)
    {
        // A second press while the first opens the agent reports success, so the page shows no false refusal.
        if (!EnterTurn())
            return true;

        try
        {
            return await RevisedAsync(agentID, cancellationToken);
        }
        finally
        {
            LeaveTurn();
        }
    }

    /// <summary>
    /// Opens one agent of the domain for correction, with the turn already claimed.
    /// </summary>
    private async Task<bool> RevisedAsync(string agentID, CancellationToken cancellationToken)
    {
        // Without a Draft there is no domain to take the agent out of.
        if (draftStateService.Current is not { } draft)
            return false;

        // The agent and the intent that routes to it, both by the agent's intent name.
        AgentDraft? agentDraft = draft.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.ID, agentID, StringComparison.OrdinalIgnoreCase));

        IntentDraft? intentDraft = draft.Intents.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, agentID, StringComparison.OrdinalIgnoreCase));

        // Both or neither. An agent whose intent is missing is a domain that would not start at all;
        // opening a section of it would have the client polishing something the classifier can
        // never reach. Review is where that is reported and repairing it is not this gesture's work.
        if (agentDraft is null || intentDraft is null)
            return false;

        // The edit remembers where the agent stood in both lists, so accepting it puts it back in its place under its origin.
        InterviewState interviewState = new InterviewState
        {
            At = 0,
            Agent = agentDraft,
            Revision = new AgentRevision
            {
                IntentAt = draft.Intents.IndexOf(intentDraft),
                AgentAt = draft.Agents.IndexOf(agentDraft),
                Origin = agentDraft.Origin
            }
        };

        // A map of one and it is the honest one: this interview is about this agent and stops when
        // it is back in the domain. The rest of the domain is still on the screen, the strip under
        // the rail showing every intent that is not on today's map, so the client sees their whole
        // domain with the one they opened lit inside it. The fingerprint tells later whether the edit changed anything.
        interviewState.Map.Add(intentDraft);
        interviewState.Revision!.Was = Fingerprint(interviewState);

        // Out of the domain for the length of the edit, so accepting it cannot write the agent twice.
        draft.Agents.Remove(agentDraft);
        draft.Intents.Remove(intentDraft);
        draftStateService.Set(draft);

        // The edit becomes the interview the page shows.
        Current = interviewState;

        // Always the Target, never the section the client meant to fix. An edit is a compose walk
        // seeded from an existing agent rather than a blank one, so it must start where a compose
        // does and let the chain in AnswerAsync decide how far it goes — which is what makes a
        // change to one section force the model to recheck every section downstream of it, instead
        // of trusting a change can't have moved the ground under them.
        await EnterPassAsync(interviewState, InterviewStep.AgentTarget, cancellationToken, revisiting: true);

        // The agent is open at its Target.
        return true;
    }

    /// <summary>Opens an agent of today's map at its Target for correction, setting the interview aside until the correction hands it back.</summary>
    public async Task<bool> ReopenAsync(int entry, CancellationToken cancellationToken = default)
    {
        // A second press while the first opens the agent reports success, so the page shows no false refusal.
        if (!EnterTurn())
            return true;

        try
        {
            return await ReopenedAsync(entry, cancellationToken);
        }
        finally
        {
            LeaveTurn();
        }
    }

    /// <summary>Opens one agent of today's map for correction, with the turn already claimed.</summary>
    private async Task<bool> ReopenedAsync(int entry, CancellationToken cancellationToken)
    {
        // One correction at a time and never over the map, which is settled against itself alone.
        if (Current is not { Resume: null, Revision: null } frontier
            || frontier.Pass == InterviewStep.DomainMapper
            || entry < 0 || entry >= frontier.Map.Count || entry > frontier.At)
            return false;

        // The entry's intent, the agent to correct and where it will go back, settled by the two branches below.
        IntentDraft intentDraft = frontier.Map[entry];
        AgentDraft? agentDraft;
        AgentRevision revision;

        // The entry the interview stands on holds its agent in hand rather than in the domain.
        if (frontier.OnAnEntry && entry == frontier.At)
        {
            // The agent in hand is in no list of the domain: nothing leaves the Draft.
            agentDraft = frontier.Agent;
            revision = new AgentRevision { Origin = agentDraft.Origin };
        }
        else
        {
            // An earlier entry was accepted into the domain, so it is taken from there.
            if (draftStateService.Current is not { } draft)
                return false;

            agentDraft = draft.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.ID, intentDraft.Name, StringComparison.OrdinalIgnoreCase));

            IntentDraft? committed = draft.Intents.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, intentDraft.Name, StringComparison.OrdinalIgnoreCase));

            // An entry whose agent is missing from the domain has nothing to correct.
            if (agentDraft is null || committed is null)
                return false;

            // Out of the domain for the length of the correction, so accepting it cannot write it twice.
            revision = new AgentRevision
            {
                IntentAt = draft.Intents.IndexOf(committed),
                AgentAt = draft.Agents.IndexOf(agentDraft),
                Origin = agentDraft.Origin
            };

            draft.Agents.Remove(agentDraft);
            draft.Intents.Remove(committed);
            draftStateService.Set(draft);

            // The committed intent is the one the correction will write back.
            intentDraft = committed;
        }

        // The correction is an interview of its own over one agent, holding the one it set aside.
        InterviewState correction = new InterviewState
        {
            At = 0,
            Agent = agentDraft,
            Revision = revision,
            Resume = frontier
        };

        // A map of one, fingerprinted so accepting it knows whether anything changed.
        correction.Map.Add(intentDraft);
        revision.Was = Fingerprint(correction);

        Current = correction;

        // A correction starts at the Target and chains forward like any edit.
        await EnterPassAsync(correction, InterviewStep.AgentTarget, cancellationToken, revisiting: true);

        return true;
    }

    /// <summary>Hands the interview back to the frontier a correction set aside.</summary>
    private async Task ResumeAsync(InterviewState correction, InterviewState frontier, CancellationToken cancellationToken)
    {
        // The agent in hand carries what the correction wrote; a resumed save file holds two copies of it.
        if (HoldsInHand(correction, frontier))
            frontier.Agent = correction.Agent;

        // The answer box is primed once per exchange, so the count must never run backwards.
        frontier.Exchanges = Math.Max(frontier.Exchanges, correction.Exchanges);
        Current = frontier;

        // An agent that was waiting for the client's word waits again: the correction rechecked everything after it.
        if (frontier is { Pass: InterviewStep.AgentFormatting, ReadyForReview: true })
        {
            Keep(frontier);
            return;
        }

        // The step the interview stood on, reopened over whatever the correction changed.
        await EnterPassAsync(frontier, frontier.Pass, cancellationToken, revisiting: true);
    }

    /// <summary>Whether the agent under correction is the one the set-aside interview holds in hand.</summary>
    private static bool HoldsInHand(InterviewState correction, InterviewState frontier) =>
        frontier.OnAnEntry
        && string.Equals(frontier.Intent.Name, correction.Intent.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The step the set-aside interview stood on, acceptance counted as the one after Formatting.</summary>
    private static int StandsAt(InterviewState frontier) =>
        frontier is { Pass: InterviewStep.AgentFormatting, ReadyForReview: true }
            ? (int)InterviewStep.AgentFormatting + 1
            : (int)frontier.Pass;

    /// <summary>Records the client's answer and asks the next question, crossing into the next pass when the running one is settled.</summary>
    public async Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default)
    {
        // An answer arriving while a turn runs is the same answer again: the page sees the interview as it stands.
        if (!EnterTurn())
            return Current ?? new InterviewState();

        try
        {
            return await AnsweredAsync(answer, cancellationToken);
        }
        finally
        {
            LeaveTurn();
        }
    }

    /// <summary>One answer, carried through every pass it settles.</summary>
    private async Task<InterviewState> AnsweredAsync(string answer, CancellationToken cancellationToken)
    {
        // Answering when nothing is open starts the interview.
        InterviewState interviewState = Current ?? await StartCoreAsync(cancellationToken);

        // An empty answer tells the model nothing and spends no turn.
        if (string.IsNullOrWhiteSpace(answer))
            return interviewState;

        // The model reads the answer and writes through its tools.
        await ExchangeAsync(interviewState, answer, cancellationToken);

        // Once the state machine has confirmed the pass settled (SetPassCompleted is checked, never believed),
        // the next pass opens in the same turn: the boundary is Alembic's and the client simply gets the next question.
        // It loops because a pass may settle the moment it opens, such as a toolkit already described in full;
        // EnterPassAsync clears ReadyForReview, so each round takes a fresh confirmation. It stops at Formatting,
        // since letting the agent into the domain is the client's decision. An edit walks the same chain in Correcting
        // mode, so a change to one section makes the model recheck every section after it.
        while (interviewState is { ReadyForReview: true, Error: null, Pass: not InterviewStep.AgentFormatting })
        {
            // A correction of the agent in hand stops where the interview stood: from there on the
            // agent was never written, so there is nothing left to correct.
            if (interviewState.Resume is { } frontier
                && HoldsInHand(interviewState, frontier)
                && (int)interviewState.Pass + 1 >= StandsAt(frontier))
            {
                await ResumeAsync(interviewState, frontier, cancellationToken);
                return Current!;
            }

            // Leaving the map is also stepping onto its first entry. The mapping pass settles no
            // agent, so there would be nothing for the functional pass to be about otherwise.
            if (interviewState.Pass == InterviewStep.DomainMapper)
                interviewState.At = 0;

            // The next pass, which asks its opening question in this same turn.
            await EnterPassAsync(interviewState, interviewState.Pass + 1, cancellationToken);
        }

        // The interview as the page draws it after this answer.
        return interviewState;
    }

    /// <summary>Folds the agent just finished into the Draft, then moves the interview to the next entry of the map.</summary>
    public async Task<bool> AcceptAsync(CancellationToken cancellationToken = default)
    {
        // A second press of Accept while the first runs reports the interview as going on.
        if (!EnterTurn())
            return true;

        try
        {
            return await AcceptedAsync(cancellationToken);
        }
        finally
        {
            LeaveTurn();
        }
    }

    /// <summary>
    /// Lets the agent in hand into the domain, with the turn already claimed.
    /// </summary>
    private async Task<bool> AcceptedAsync(CancellationToken cancellationToken)
    {
        // No interview, no agent in hand to let in.
        if (Current is not { } interviewState)
            return false;

        // The agent in hand never left the interview it was reopened from, so it goes back there
        // rather than into the domain.
        if (interviewState.Resume is { } holding && HoldsInHand(interviewState, holding))
        {
            await ResumeAsync(interviewState, holding, cancellationToken);
            return true;
        }

        // A domain born in this interview gets its Draft with its first agent.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // An edit carries where the agent stood; a fresh agent carries none.
        AgentRevision? revision = interviewState.Revision;

        // Authored, revised or kept as imported, by whether anything changed, which the migration report states.
        Provenance origin = Committed(revision, interviewState);

        interviewState.Intent.Origin = origin;
        interviewState.Agent.Origin = origin;

        // Only where there is none: a fresh agent takes its intent's name, while one from the client's own configuration
        // already reaches its intent case-insensitively, 'Contract' answering 'contract'. Rewriting it to the intent's
        // casing would be an edit nobody asked for, showing in the client's diff and in the migration report.
        if (string.IsNullOrWhiteSpace(interviewState.Agent.ID))
            interviewState.Agent.ID = interviewState.Intent.Name;

        // An edited agent keeps the C# facts it arrived with. They may not be a guess at all — a save
        // file and an archive both carry the real namespace, the real class names and the tier — and
        // writing a fresh inference over a fact, then flagging the result Inferred, would lose
        // something Alembic cannot get back and announce the loss as an improvement.
        if (revision is null)
        {
            interviewState.Agent.Code.Inferred = true;
            interviewState.Agent.Code.AgentClassName = AgentCodeFacts.ProposeClassName(interviewState.Intent.Name, "Agent");

            // An agent with no native tools gets no tool class and that is a legal shape rather than
            // a gap: an MCP-only agent's tools arrive at runtime and never appear in a generated tool class.
            interviewState.Agent.Code.ToolClassName = interviewState.Agent.Tools.Count > 0
                ? AgentCodeFacts.ProposeClassName(interviewState.Intent.Name, "Tool")
                : null;
        }
        else if (interviewState.Agent.Tools.Count > 0 && interviewState.Agent.Code.ToolClassName is null)
        {
            // The one C# fact an edit can genuinely create: an agent that had no native tools and now
            // has some has nowhere for them to be emitted. Proposed by the same convention as a fresh
            // one and nothing else about the record is touched.
            interviewState.Agent.Code.ToolClassName = AgentCodeFacts.ProposeClassName(interviewState.Intent.Name, "Tool");
        }

        // Back where it stood when it was taken out for the edit, at the end of the lists when it is new.
        Restore(draft.Intents, revision?.IntentAt ?? -1, interviewState.Intent);
        Restore(draft.Agents, revision?.AgentAt ?? -1, interviewState.Agent);

        // An agent corrected behind the frontier is back in its place and the interview resumes there.
        if (interviewState.Resume is { } frontier)
        {
            draftStateService.Set(draft);
            await ResumeAsync(interviewState, frontier, cancellationToken);
            return true;
        }

        // The map is spent the moment this was the last of it and an interview nobody is having is
        // not something to resume into.
        draft.Sitting = interviewState.At + 1 < interviewState.Map.Count
            ? new InterviewSitting
            {
                Pass = InterviewStep.AgentTarget,
                At = interviewState.At + 1,
                Map = [.. interviewState.Map],
                Agent = new AgentDraft()
            }
            : null;

        // Re-setting the same instance is what raises Changed, so a page showing the Draft
        // re-renders whether or not the Draft object itself was new.
        draftStateService.Set(draft);

        // Down the map, not out of the interview. The map is the promise the client made when they
        // said what their business gets asked and an interview that stopped at the first agent
        // would leave them to remember the rest of their own list.
        if (interviewState.At + 1 < interviewState.Map.Count)
        {
            interviewState.At++;
            interviewState.Agent = new AgentDraft();

            await EnterPassAsync(interviewState, InterviewStep.AgentTarget, cancellationToken);
            return true;
        }

        // The map is spent and the interview with it. Which agent may consult which is settled on the
        // Collaboration form over the whole domain, where a colleague at another Morgana can be named too.
        Abandon();
        return false;
    }

    /// <summary>Steps the interview back one step of the journey, keeping everything already written.</summary>
    public async Task<bool> BackAsync(CancellationToken cancellationToken = default)
    {
        // A second press of Back while the first runs is the same step back, reported as taken.
        if (!EnterTurn())
            return true;

        try
        {
            return await SteppedBackAsync(cancellationToken);
        }
        finally
        {
            LeaveTurn();
        }
    }

    /// <summary>Steps back into the previous part of the interview, with the turn already claimed.</summary>
    private async Task<bool> SteppedBackAsync(CancellationToken cancellationToken)
    {
        // No interview, nothing behind it.
        if (Current is not { } interviewState)
            return false;

        // An agent waiting to be let in steps back into the step it is waiting on rather than the one
        // before it: the client is looking at what was just written and wants to change a line of it.
        // That is always Formatting, since composing and correcting both chain through every pass and
        // AnswerAsync's loop moves past any earlier pass the moment it settles.
        if (interviewState is { ReadyForReview: true, Pass: InterviewStep.AgentFormatting })
        {
            await EnterPassAsync(interviewState, interviewState.Pass, cancellationToken, revisiting: true);
            return true;
        }

        // Any other pass steps back to the one before it on the same agent.
        switch (interviewState.Pass)
        {
            // The map is the first thing there is. Behind it is the landing, which is not a step.
            case InterviewStep.DomainMapper:
                return false;

            // An edit is one agent and no map: the client opened it from the walk at its Target and
            // behind that first pass is that screen rather than a step of anything. Where they came
            // in is where they leave and the walk's own way back is what takes them.
            case InterviewStep.AgentTarget when interviewState.Revision is not null:
                return false;

            // Out of the first entry is back onto the map itself. The entries survive, being the
            // map rather than this pass's working copy, so the mapper reads its own list back and the
            // client changes what they came to change.
            case InterviewStep.AgentTarget when interviewState.At == 0:
                interviewState.At = -1;
                await EnterPassAsync(interviewState, InterviewStep.DomainMapper, cancellationToken, revisiting: true);
                return true;

            // Out of an entry into the one before it, which is in the domain already. It comes back
            // out of the Draft and into the client's hands: an agent being revised must not also be
            // sitting in the configuration: committing it a second time would write it twice.
            case InterviewStep.AgentTarget:
                interviewState.At--;
                Uncommit(interviewState);
                await EnterPassAsync(interviewState, InterviewStep.AgentFormatting, cancellationToken, revisiting: true);
                return true;

            // A later pass of the same agent steps back to the pass before it.
            default:
                await EnterPassAsync(interviewState, interviewState.Pass - 1, cancellationToken, revisiting: true);
                return true;
        }
    }

    /// <summary>
    /// What Alembic asked the client this turn: the last assistant message that asks something, else the last thing said.
    /// A run joins every assistant message and often ends narrating its own work, so the question-mark test, the law the interviewer is held to in prose,
    /// keeps the real question on screen; a turn that asks nothing is a defect and showing it is how anyone finds out.
    /// </summary>
    private static string LastAsked(AgentResponse response)
    {
        // Everything the model said to the client this run, in order, without empty messages.
        string[] said = [.. response.Messages
            .Where(message => message.Role == ChatRole.Assistant)
            .Select(message => message.Text?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)];

        // The last of those that asks something.
        string? asked = said.LastOrDefault(text => text.Contains('?'));

        // The question, else the last thing said, else whatever text the run carries.
        return asked ?? said.LastOrDefault() ?? response.Text?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Writes where the interview stands into the Draft at every turn, so the file behind Save my work is never out of date.
    /// A save the client has to remember is remembered after the tab is gone; keeping it current helps somebody who closed a window by accident.
    /// </summary>
    private void Keep(InterviewState interviewState)
    {
        // The first turn of a new domain may come before any Draft exists.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // The interview as it stands, with the one a correction set aside where there is one.
        draft.Sitting = SittingOf(interviewState);
        draft.Sitting.Resume = interviewState.Resume is { } frontier ? SittingOf(frontier) : null;

        // Every page reading the Draft re-renders on it.
        draftStateService.Set(draft);
    }

    /// <summary>The facts of one interview that have to survive a closed tab.</summary>
    private static InterviewSitting SittingOf(InterviewState interviewState) => new()
    {
        Pass = interviewState.Pass,
        At = interviewState.At,
        Map = [.. interviewState.Map],
        Agent = interviewState.Agent,
        Example = interviewState.Example,
        Revision = interviewState.Revision
    };

    /// <summary>An interview set aside, rebuilt from its sitting without asking the model anything, to be entered when a correction hands it back.</summary>
    private static InterviewState StateOf(InterviewSitting sitting)
    {
        // The fields the sitting saved, the map copied in below.
        InterviewState interviewState = new InterviewState
        {
            Pass = sitting.Pass,
            At = sitting.At,
            Agent = sitting.Agent,
            Example = sitting.Example,
            Revision = sitting.Revision
        };

        interviewState.Map.AddRange(sitting.Map);

        return interviewState;
    }

    /// <summary>Takes the entry the interview has just stepped back onto out of the Draft and back into hand.</summary>
    private void Uncommit(InterviewState interviewState)
    {
        // Without a Draft nothing was committed to take back.
        if (draftStateService.Current is not { } draft)
            return;

        // The entry's agent as it was accepted, by its intent's name.
        AgentDraft? committed = draft.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.ID, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase));

        // Back in hand with everything written for it, so the client changes it rather than rewriting it.
        if (committed is not null)
        {
            draft.Agents.Remove(committed);
            interviewState.Agent = committed;
        }

        // The intent leaves the domain with it, so accepting the entry again cannot write it twice.
        draft.Intents.RemoveAll(intent =>
            string.Equals(intent.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase));

        draftStateService.Set(draft);
    }

    /// <summary>Discards the interview without touching the Draft, dropping the pass's agent and session with it.</summary>
    public void Abandon()
    {
        // Nothing of the interview survives but what it already wrote into the Draft.
        Current = null;
        agent = null;
        session = null;
    }

    /// <summary>
    /// Moves the interview into a pass: a new agent, a new session and the pass's opening question.
    /// A session carried over would spend every later turn re-arguing decisions already taken, so only the configuration crosses,
    /// read back as settled fact; the client's transcript stays one interview and only the model starts again.
    /// </summary>
    private async Task EnterPassAsync(
        InterviewState interviewState,
        InterviewStep interviewPass,
        CancellationToken cancellationToken,
        bool revisiting = false)
    {
        // The new pass starts unsettled, its exchanges counted from here for the page's step indicator.
        interviewState.Pass = interviewPass;
        interviewState.ReadyForReview = false;
        interviewState.PassOpenedAt = interviewState.Exchanges;

        // An agent is about its intent from its first pass, not from the moment it is let in. Every
        // mid-interview check reading the pair — the findings, the recap, the colleague rules — was
        // otherwise looking at an agent no intent could reach and reporting the very agent being
        // written as unhandled. Only while it is blank: one out of the client's own configuration
        // arrives with an ID that already reaches it and Morgana matches the two case-insensitively.
        if (interviewState.OnAnEntry && string.IsNullOrWhiteSpace(interviewState.Agent.ID))
            interviewState.Agent.ID = interviewState.Intent.Name;

        // Correcting is a fact of the agent, not of how this call was reached. An edit chained into
        // from a settled earlier pass stands on an agent that already exists exactly as much as one
        // reopened directly, so both read Correcting. revisiting is narrower: DomainMapper alone
        // needs it, below, to tell "begin" from "reopen".
        bool correcting = interviewState.Revision is not null;

        // A fresh agent and session holding this pass's tools and prompt.
        await BuildAgentAsync(interviewState, interviewPass, correcting);

        // The opening question of the whole interview is nobody's to phrase but the client's own:
        // there is nothing yet for a model to be reading back, so a model asked for it is either
        // repeating the one sentence that fits or drifting off it. Fixed rather than composed;
        // the session still needs building above, since the client's first answer lands on it.
        if (interviewPass == InterviewStep.DomainMapper && !revisiting)
        {
            interviewState.Question = OpeningQuestion;
            interviewState.Choice = null;
            interviewState.Example = null;
            interviewState.Placing = null;
            interviewState.Quoted = null;
            interviewState.Traits = [];
            interviewState.Chosen = false;
            interviewState.Exchanges++;

            // ExchangeAsync always ends in Keep, precisely so the Draft behind Save my work is true
            // the moment a question is on screen, the very first one included, before the client
            // has said anything. Skipping the model here must not also skip that.
            Keep(interviewState);
            return;
        }

        // The pass opens by being told where it stands and answers with its first question.
        await ExchangeAsync(interviewState, Bootstrap(interviewState, interviewPass, correcting), cancellationToken);
    }

    /// <summary>
    /// The one question every interview opens on, fixed rather than composed.
    /// The client has said nothing yet, so there is nothing for a model to phrase differently from one run to the next.
    /// </summary>
    private const string OpeningQuestion =
        "Morgana takes on a handful of your own processes, one entry each becoming its own agent — "
        + "what would you like her to handle for you?";

    /// <summary>
    /// What the agent is sent to open a pass: which entry of how many, which intent, what is written on the agent and what this step gives it.
    /// A pass left to work its standing out spends a tool call or guesses, while the client is owed a first sentence naming their agent and this stage.
    /// </summary>
    private string Bootstrap(InterviewState interviewState, InterviewStep interviewPass, bool correcting)
    {
        // The map has no entry to stand on: it opens on the business alone.
        if (interviewPass == InterviewStep.DomainMapper)
            return BootstrapMessages[interviewPass] + Known(interviewState);

        // Which of the two jobs this is, stated as a fact rather than an instruction: what to do about
        // each is one rule in the shared layer and repeating it here would be a second copy drifting
        // from the first. Every way into a pass over a written agent is the same situation to the pass:
        // stepping back, resuming, editing from the walk or the chain moving on.
        string standing = correcting
            ? Reads(interviewState.Agent)
            : Stands(interviewState.Agent);

        // The entry, its intent and the map's sentence for it, the standing, the step's purpose and what is known of the trade.
        return $"The map is settled and the client is still here. You are on entry {interviewState.At + 1} "
               + $"of {interviewState.Map.Count}: the intent '{interviewState.Intent.Name}', which the map "
               + $"describes as: {interviewState.Intent.Description}. "
               + standing + " "
               + BootstrapMessages[interviewPass]
               + Known(interviewState);
    }

    /// <summary>
    /// What is already known about the client's work, handed to a step before it asks anything, as fact to build questions on.
    /// Every pass is a fresh session, so a step knowing only the configuration would ask a shopkeeper what a system ought to check, never told there is a shop.
    /// </summary>
    private string Known(InterviewState interviewState)
    {
        // The business, the agent in hand and the other agents of the domain.
        List<KnownFact> trade = [.. (draftStateService.Current?.Learned ?? [])];
        List<KnownFact> agent = interviewState.OnAnEntry ? [.. interviewState.Agent.Known] : [];
        List<string> neighbours = Neighbours(interviewState);

        // Nothing known adds no paragraph to the opening message.
        if (trade.Count == 0 && agent.Count == 0 && neighbours.Count == 0)
            return string.Empty;

        // Facts to build questions on, never to ask again or to copy into a section.
        string told = " What is known about their work — build your questions on it, never ask any of "
                      + "it again and never copy any of it into a section:";

        // The trade first, then the agent in hand, nearest the question since it is what this step is about.
        if (trade.Count > 0)
            told += "\nAbout their business: " + Facts(trade);

        if (agent.Count > 0)
            told += "\nAbout this agent: " + Facts(agent);

        // The other agents arrive as subjects rather than as sentences: nine of them read whole would
        // be forty sentences carried into every question, nearly all about counters this step will
        // never touch. What is listed is enough to know whether one of them bears on the question in
        // hand, which is the only moment the sentences themselves are worth fetching.
        if (neighbours.Count > 0)
            told += "\nThe other agents of this domain and what is on record about each — call RecallAgent "
                    + "with a name to read one, where what it keeps bears on the question you are about "
                    + "to ask: " + string.Join(" ", neighbours);

        // Said and read are not worth the same and the difference is stated once, at the foot of the
        // list: an agent that arrived in an upload carries finished prose and no memory of the
        // conversation behind it, so everything known about it is Alembic's own reading.
        if (trade.Concat(agent).Any(fact => fact.Inferred))
            told += "\nWhat is marked (read, not said) was taken off the configuration they uploaded and "
                    + "nobody has confirmed it. Treat it as a reading that may be wrong: let it be "
                    + "corrected inside a question you were going to ask anyway and never put it to them "
                    + "as something they told you.";

        // The paragraph the opening message ends on.
        return told;
    }

    /// <summary>A run of facts as a step reads them, each saying whether anybody actually said it.</summary>
    private static string Facts(IEnumerable<KnownFact> facts) =>
        string.Join(" ", facts.Select(fact =>
            $"- {fact.Subject}: {fact.Fact}" + (fact.Inferred ? " (read, not said)" : string.Empty)));

    /// <summary>
    /// What is known about every other agent of the domain, named one by one, the written ones and the entries still ahead.
    /// An agent is only itself beside its neighbours: a step seeing one counter alone claims ground twice and draws boundaries against nothing.
    /// </summary>
    private List<string> Neighbours(InterviewState interviewState)
    {
        // A domain born in this interview may have no Draft yet.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // What each written agent keeps on record, by its intent's name.
        Dictionary<string, List<KnownFact>> written = draft.Agents
            .Where(agent => agent.ID is not null)
            .ToDictionary(agent => agent.ID!, agent => agent.Known, StringComparer.OrdinalIgnoreCase);

        // One line per neighbour.
        List<string> others = [];

        // The domain's intents and today's map, which still holds the entries nobody has opened.
        foreach (IntentDraft intent in draft.Intents.Concat(interviewState.Map))
        {
            // A nameless entry, the agent in hand or a neighbour already listed adds no line.
            if (intent.Name is not { Length: > 0 } name
                || string.Equals(name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase)
                || others.Any(line => line.StartsWith($"- {name}:", StringComparison.OrdinalIgnoreCase)))
                continue;

            // The subjects on record, else the map's sentence for it: enough to tell whether it bears on the question.
            string about = written.TryGetValue(name, out List<KnownFact>? facts) && facts.Count > 0
                ? string.Join(", ", facts.Select(fact => fact.Subject).Distinct(StringComparer.OrdinalIgnoreCase))
                : intent.Description ?? "nothing known about it yet";

            others.Add($"- {name}: {about}");
        }

        // The lines the opening message lists the neighbours by, each one a name RecallAgent can be called with.
        return others;
    }

    /// <summary>
    /// The agent as it stands, whole, handed to a step reopening it in its opening message rather than left to a tool call.
    /// A correction goes wrong where a section is changed against others it no longer fits, so the step needs all of it in view from the first sentence;
    /// <c>GetAgentSoFar</c> remains how a later turn re-reads what has since been rewritten.
    /// </summary>
    private static string Reads(AgentDraft agent)
    {
        // Each written section in the agent's own order, in plain words.
        List<string> sections = [];

        if (AgentRows.Plain(agent.Target) is { Length: > 0 } target)
            sections.Add($"Target: {target}");

        if (AgentRows.Plain(agent.Personality) is { Length: > 0 } personality)
            sections.Add($"Personality: {personality}");

        if (agent.Tools.Count > 0)
            sections.Add($"Toolkit: {string.Join(", ", agent.Tools.Select(tool => tool.Name ?? "(unnamed)"))}");

        if (AgentRows.Plain(agent.Instructions) is { Length: > 0 } instructions)
            sections.Add($"Instructions: {instructions}");

        if (AgentRows.Plain(agent.Formatting) is { Length: > 0 } formatting)
            sections.Add($"Formatting: {formatting}");

        // A step reopened over an agent nobody has written anything on yet, a sitting saved mid-first
        // answer: there is nothing to read back and the ordinary sentence about what stands is the
        // true one.
        if (sections.Count == 0)
            return Stands(agent);

        // The exact two shapes the shared layer tells every pass to expect. They are one sentence in
        // two places, so the wording is the same wording: a pass that has been taught to recognise
        // "already exists and reads" must be handed those words and not a paraphrase of them.
        return "This agent already exists and you are correcting it. It currently reads — "
               + string.Join(" | ", sections);
    }

    /// <summary>
    /// What is already written on the agent the step is opening on, read off the agent and never off which pass is running.
    /// Stepping back, resuming and editing reach a pass over differently filled agents, so a sentence fixed per pass would tell the model it starts from nothing
    /// when it does not; a missing Toolkit goes unmentioned, since no native tools is also the legal shape of an agent served by MCP.
    /// </summary>
    private static string Stands(AgentDraft agent)
    {
        // The sections the steps before this one settled, in the order a compose writes them.
        List<string> written = [];

        if (!string.IsNullOrWhiteSpace(agent.Target))
            written.Add("its Target");

        if (!string.IsNullOrWhiteSpace(agent.Personality))
            written.Add("its Personality");

        if (agent.Tools.Count > 0)
            written.Add("its Toolkit");

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
            written.Add("its Instructions");

        if (!string.IsNullOrWhiteSpace(agent.Formatting))
            written.Add("its Formatting");

        // Either a blank agent or the sections the step can build on.
        return written.Count == 0
            ? "Nothing of this agent is written yet."
            : $"Nothing of this agent is written yet beyond what the steps before you settled: {string.Join(", ", written)}.";
    }

    /// <summary>
    /// Assembles the agent for one pass: its prompt and only the tools that pass is allowed.
    /// What a pass may write is settled by the tools its phase class holds rather than by a sentence asking it to abstain, so the mode reaches the prompt and never the toolset.
    /// </summary>
    private async Task BuildAgentAsync(InterviewState interviewState, InterviewStep interviewPass, bool correcting)
    {
        // The pass's prompt ID composes its instructions and names its agent and the tool calls it logs.
        string interviewerId = PassPromptIds[interviewPass];

        // Correcting a Toolkit and writing one both come down to DeclareTool and DropTool, so the
        // toolset never depends on the mode: a pass that could not drop a tool while correcting could
        // not do what it was reopened for. A step outside the seven interview passes holds no tools.
        InterviewTool interviewTool = interviewPass switch
        {
            // Maps the domain into its kinds of request.
            InterviewStep.DomainMapper => new DomainMapperTool(interviewState, draftStateService, draftValidationService, recapService),

            // Writes an agent's Target.
            InterviewStep.AgentTarget => new AgentTargetTool(interviewState, draftStateService, draftValidationService, recapService),

            // Writes an agent's Personality.
            InterviewStep.AgentPersonality => new AgentPersonalityTool(interviewState, draftStateService, draftValidationService, recapService),

            // Declares an agent's tools.
            InterviewStep.AgentToolkit => new AgentToolkitTool(interviewState, draftStateService, draftValidationService, recapService),

            // Writes what a colleague reads before consulting an agent.
            InterviewStep.AgentTerritory => new AgentTerritoryTool(interviewState, draftStateService, draftValidationService, recapService),

            // Writes an agent's Instructions.
            InterviewStep.AgentInstructions => new AgentInstructionsTool(interviewState, draftStateService, draftValidationService, recapService),

            // Writes an agent's Formatting.
            InterviewStep.AgentFormatting => new AgentFormattingTool(interviewState, draftStateService, draftValidationService, recapService),

            // The step has no interview pass to hold tools for.
            _ => throw new InvalidOperationException($"The step '{interviewPass}' is not an interview pass and holds no tools.")
        };

        // The composer gives each tool the description its attributes declare.
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter(promptComposerService);

        // The pass's own tools first, in the order its phase class declares them.
        toolAdapter.AddTools(interviewTool, MorganaToolAdapter.ProjectToolDefinitions(interviewTool.GetType()));

        // Then the four that are the same in every pass, declared once on the base class.
        toolAdapter.AddTools(interviewTool, MorganaToolAdapter.ProjectToolDefinitions(typeof(InterviewTool)));

        // Efficiency, resolved directly rather than through CompleteWithSystemPromptAsync, which
        // runs on the framework tier. Writing non-contradictory dispositive prose is the exact task
        // the Economy tier is weakest at.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = $"alembic-{interviewerId.ToLowerInvariant()}",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                // Morgana's layer over Alembic's, in the job this step is doing.
                Instructions = await alembicPromptService.ComposeAsync(interviewerId, correcting),

                // Each one wrapped so the call and the sentence it is answered with are written
                // down. A tool refusing what a pass asked of it says so to the model alone, which
                // is the whole design. Left at that, a pass that told the client something was
                // settled while its own tool had refused reads exactly like one that succeeded.
                Tools = [.. (await toolAdapter.CreateAllFunctionsAsync())
                                              .Select(function => new LoggedTool(function, logger, interviewerId))]
            }
        });

        // A session of its own: the pass remembers nothing of the one before it.
        session = await agent.CreateSessionAsync();
    }

    /// <summary>One round trip: the client's words in, Alembic's words out and whatever its tools wrote along the way already in the state.</summary>
    private async Task ExchangeAsync(InterviewState interviewState, string message, CancellationToken cancellationToken)
    {
        // A pass not yet built has no model to send the words to.
        if (agent is null || session is null)
            return;

        // What the last turn showed and offered is cleared, so only this turn's tools can set it again.
        interviewState.Error = null;
        interviewState.PendingChoice = null;
        interviewState.PendingExample = null;
        interviewState.PendingPlacing = null;
        interviewState.PendingQuoted = null;
        interviewState.PendingTraits.Clear();
        interviewState.Changed.Clear();

        // A before/after diff of every field Snapshot exposes, rather than trusting the tools
        // themselves to report what they touched: a tool answers the model, it does not report to
        // the UI, so this is the one place that knows which panel rows actually changed this turn.
        Dictionary<string, string?> before = interviewState.Snapshot();

        long startedAt = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "{Pass} is answering {Length} characters from the client",
            PassPromptIds[interviewState.Pass], message.Length);

        // The turn's own deadline beside the client's Stop, so a provider that stalls ends the wait instead of holding the page forever.
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(turnCeiling);

        try
        {
            // The model reads the words, calls the pass's tools and answers.
            AgentResponse response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, message), session, cancellationToken: bounded.Token);

            logger.LogInformation(
                "{Pass} answered in {Elapsed:0.0}s",
                PassPromptIds[interviewState.Pass], Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            // The fields whose value moved this turn, which the page lights up.
            foreach ((string field, string? value) in interviewState.Snapshot())
                if (!string.Equals(value, before[field], StringComparison.Ordinal))
                    interviewState.Changed.Add(field);

            // The question this turn asked, without the model's narration of its own work.
            string asked = LastAsked(response);

            // A turn spent wholly on tool calls is legitimate and the question already on screen stands. Only the question
            // text waits on that: the choice, example, placing, quote and traits are promoted regardless, since a turn may
            // offer a button for the question already on screen and the pending values are cleared at the top of every
            // exchange, so a button held back now could never come back.
            if (asked.Length > 0)
            {
                interviewState.Question = asked;
                interviewState.Exchanges++;
            }

            interviewState.Choice = interviewState.PendingChoice;
            interviewState.Example = interviewState.PendingExample;
            interviewState.Placing = interviewState.PendingPlacing;
            interviewState.Quoted = interviewState.PendingQuoted;
            interviewState.Traits = [.. interviewState.PendingTraits];
            interviewState.Chosen = false;
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The turn's own deadline ran out: the client is told their work is safe and to send the answer again.
            logger.LogError(
                "{Pass} was still waiting after {Elapsed:0.0}s and was given up on",
                PassPromptIds[interviewState.Pass], Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            interviewState.Error = "That turn took too long and was given up on. Your work is safe — send the answer again.";
        }
        // Neither the turn's ceiling nor the client stopped this turn: a call beneath it gave up on its
        // own, so blaming the ceiling would send whoever reads the log after the wrong setting.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "{Pass} was cancelled beneath the turn after {Elapsed:0.0}s, inside its ceiling",
                PassPromptIds[interviewState.Pass], Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            interviewState.Error = "The model stopped answering before the turn was done. Your work is safe — send the answer again.";
        }
        catch (Exception ex)
        {
            // Any other failure is shown on the page, the interview standing where it was.
            logger.LogError(ex, "The interview turn failed after {Elapsed:0.0}s", Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
            interviewState.Error = $"The turn could not be completed: {ex.Message}";
        }

        // Every turn, the one that opens a pass included, since the file behind Save my work has to be true the
        // moment a question is on screen, the very first one before the client has said anything among them.
        // After the catches too: an interview that has just failed a turn is precisely one somebody wants to take away.
        Keep(interviewState);
    }

    /// <summary>
    /// What an element's provenance becomes when the client lets it into the domain.
    /// The migration report says which of the agents the client brought were touched and can only say it if the edit says so here.
    /// </summary>
    private static Provenance Committed(AgentRevision? revision, InterviewState interviewState)
    {
        // An agent written in this interview exists in no uploaded file.
        if (revision is null)
            return Provenance.Authored;

        // Opened and left as it was. The walk lets a client read a section on their way past and
        // reading it is not touching it: what goes back is what came out, under the provenance it
        // came out with.
        if (revision.Was == Fingerprint(interviewState))
            return revision.Origin;

        // An agent written today and edited an hour later exists in no uploaded file either way, so
        // Authored stays true of it. Revised is what an imported one becomes and saying so is the
        // migration report's whole job.
        return revision.Origin == Provenance.Authored ? Provenance.Authored : Provenance.Revised;
    }

    /// <summary>Every field the interview can write, as one string, so two moments of the same agent compare without asking each field in turn.</summary>
    private static string Fingerprint(InterviewState interviewState) =>
        string.Join("\0", interviewState.Snapshot()
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => $"{field.Key}={field.Value}"));

    /// <summary>Puts an element back where the edit took it from; on the end when it was never there.</summary>
    private static void Restore<T>(List<T> list, int at, T element)
    {
        // A recorded place still inside the list keeps the client's own order; a new element joins at the end.
        if (at >= 0 && at <= list.Count)
            list.Insert(at, element);
        else
            list.Add(element);
    }

    /// <summary>
    /// One tool of the interview, with the call and the sentence it is answered with written down.
    /// Every tool answers the model, refusals included, so without the log a pass that told the client something was set while its tool had refused would read like one that succeeded.
    /// </summary>
    private sealed class LoggedTool : DelegatingAIFunction
    {
        /// <summary>How much of one argument is worth keeping: what identifies a call is the names it carried, not a paragraph of prose.</summary>
        private const int Kept = 160;

        /// <summary>Records every call with its answer.</summary>
        private readonly ILogger logger;

        /// <summary>Which pass made the call, under the name <c>alembic.json</c> declares it.</summary>
        private readonly string pass;

        /// <summary>Wraps one tool of a pass.</summary>
        public LoggedTool(AIFunction inner, ILogger logger, string pass) : base(inner)
        {
            this.logger = logger;
            this.pass = pass;
        }

        /// <summary>Runs the tool between two log lines: the call, then the answer with how long it took.</summary>
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // When the call began, for the answer's line.
            long startedAt = Stopwatch.GetTimestamp();

            logger.LogInformation("{Pass} is calling {Tool}({Arguments})", pass, Name, Written(arguments));

            // The tool itself, whose sentence goes back to the model unchanged.
            object? answer = await base.InvokeCoreAsync(arguments, cancellationToken);

            logger.LogInformation(
                "{Pass} called {Tool}({Arguments}) and was told, {Elapsed}ms later: {Answer}",
                pass, Name, Written(arguments), Stopwatch.GetElapsedTime(startedAt).Milliseconds, Short(answer?.ToString()));

            return answer;
        }

        /// <summary>The call's arguments as one line, each cut to what identifies it.</summary>
        private static string Written(AIFunctionArguments arguments) =>
            string.Join(", ", arguments.Select(argument => $"{argument.Key}: {Short(argument.Value?.ToString())}"));

        /// <summary>One value, cut short and on a single line.</summary>
        private static string Short(string? value)
        {
            // One line, so a log reader never meets a call split across several.
            string text = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();

            // Cut with an ellipsis past what identifies the value.
            return text.Length <= Kept ? text : text[..Kept] + "…";
        }
    }
}