using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
/// Conducts the interview with the client through an agent whose tools write the answers into the interview state.
/// </summary>
/// <remarks>One turn runs at a time; each pass is a fresh agent and a fresh session that reads the configuration as settled fact.</remarks>
public class InterviewService : IInterviewService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that holds the sentences every pass opens with.
    /// </summary>
    private const string AlembicPromptId = "Alembic";

    /// <summary>
    /// Composes the two-layer prompt each pass runs on from Morgana's voice and Alembic's own prose.
    /// </summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>
    /// The deterministic checks whose findings a pass reads through <c>GetFindings</c>, the same ones the validation page shows.
    /// </summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>
    /// Composes the prompt the authored agent's model will read, so a pass checks its prose through <c>GetComposedPrompt</c> before handing it over.
    /// </summary>
    private readonly IRecapService recapService;

    /// <summary>
    /// Resolves the chat client every pass runs on, always at <see cref="Records.LLMTier.Efficiency"/>.
    /// </summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// The Draft under construction for this circuit, which <see cref="Keep"/> updates every turn so the save file matches the screen.
    /// </summary>
    private readonly IDraftStateService draftStateService;

    /// <summary>The log where ignored gestures and failed turns are recorded.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// The agent conducting the current pass, rebuilt on every pass since the tools change between passes.
    /// </summary>
    private AIAgent? agent;

    /// <summary>
    /// The conversation history of the pass in progress, which no later pass inherits.
    /// </summary>
    private AgentSession? session;

    /// <inheritdoc />
    public InterviewState? Current { get; private set; }

    /// <summary>
    /// How long one turn of the interview may take before the client is told it failed.
    /// </summary>
    /// <remarks>It is generous, since it ends a stalled provider's endless wait and not a slow turn that reads a whole composed prompt.</remarks>
    private readonly TimeSpan turnCeiling;

    /// <summary>
    /// Whether a turn of this interview is already running, held as 1 while it is.
    /// </summary>
    /// <remarks>A second gesture inside the page's one-render lag is refused, since it is the same instruction arriving again.</remarks>
    private int turnClaim;

    /// <summary>Claims the interview for one turn, or reports that somebody else holds it.</summary>
    private bool EnterTurn()
    {
        // The first gesture takes the claim and runs.
        if (Interlocked.CompareExchange(ref turnClaim, 1, 0) == 0)
            return true;

        // A gesture that arrives while the claim is held is dropped, so two passes never advance one agent.
        logger.LogWarning("A second gesture arrived while a turn was still running and was ignored");
        return false;
    }

    /// <summary>Releases the claim whether the turn succeeded or failed, so the next gesture can run.</summary>
    private void LeaveTurn() => Interlocked.Exchange(ref turnClaim, 0);
    
    /// <summary>
    /// Builds the interview over the prompt composer and the checks its passes consult, with the turn ceiling read from configuration.
    /// </summary>
    public InterviewService(
        IAlembicPromptService alembicPromptService,
        IDraftValidationService draftValidationService,
        IRecapService recapService,
        ILanguageModelService llmService,
        IDraftStateService draftStateService,
        IConfiguration configuration,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
        this.llmService = llmService;
        this.draftStateService = draftStateService;
        this.logger = logger;
        // The ceiling comes from configuration so a slow provider can be given more time without a rebuild.
        turnCeiling = TimeSpan.FromSeconds(configuration.GetValue("Alembic:Work:TurnSeconds", 180));
    }

    /// <inheritdoc />
    public async Task<InterviewState> StartAsync(CancellationToken cancellationToken = default)
    {
        // A turn already running owns the interview: the caller gets the state as it stands.
        if (!EnterTurn())
            return Current ?? new InterviewState();

        try
        {
            // The opening runs with the claim held.
            return await StartCoreAsync(cancellationToken);
        }
        finally
        {
            // The claim is released even when the opening fails.
            LeaveTurn();
        }
    }

    /// <summary>
    /// Opens the interview, with the turn already claimed by whoever called.
    /// </summary>
    /// <remarks>An answer arriving before any opening starts the interview through this method, so the claim is never taken twice.</remarks>
    private async Task<InterviewState> StartCoreAsync(CancellationToken cancellationToken)
    {
        // A fresh state stands for the new interview.
        Current = new InterviewState();

        // A save file that carries a sitting resumes at its step with a fresh agent reading the written configuration as settled fact.
        if (draftStateService.Current?.Sitting is { } sitting)
        {
            // The map, the entry and the agent in hand come back as they were saved.
            Current.Map.AddRange(sitting.Map);
            Current.At = sitting.At;
            Current.Agent = sitting.Agent;

            // An edit saved half-done comes back an edit, so the agent keeps its place, provenance and C# facts when it is let in.
            Current.Revision = sitting.Revision;

            // A correction saved half-done comes back with the interview it set aside.
            if (sitting.Resume is { } frontier)
                Current.Resume = StateOf(frontier);

            // The saved pass is re-entered as a revisit, so its opening message states the job it is doing.
            await EnterPassAsync(Current, sitting.Pass, cancellationToken, revisiting: true);

            // A fresh example from the reopening turn wins; the saved one returns only when that turn wrote none.
            Current.Example ??= sitting.Example;

            // The resumed interview is handed back at the saved step.
            return Current;
        }

        // Without a sitting the interview opens on the domain map.
        await EnterPassAsync(Current, InterviewStep.DomainMapper, cancellationToken);

        // The opening question of the map is handed back.
        return Current;
    }

    /// <inheritdoc />
    public async Task<bool> ReviseAsync(
        string agentID,
        CancellationToken cancellationToken = default)
    {
        // A repeated gesture is reported as handled, since the turn it repeats is still running.
        if (!EnterTurn())
            return true;

        try
        {
            // The correction opens with the claim held.
            return await RevisedAsync(agentID, cancellationToken);
        }
        finally
        {
            // The claim is released even when the opening fails.
            LeaveTurn();
        }
    }

    /// <summary>
    /// Opens one agent of the domain for correction, with the turn already claimed.
    /// </summary>
    private async Task<bool> RevisedAsync(string agentID, CancellationToken cancellationToken)
    {
        // Without a Draft there is no domain to correct.
        if (draftStateService.Current is not { } draft)
            return false;

        // The agent and its intent are looked up by the name the client picked, as the framework matches intents.
        AgentDraft? agentDraft = draft.Agents.FirstOrDefault(a =>
            string.Equals(a.ID, agentID, StringComparison.OrdinalIgnoreCase));

        IntentDraft? intentDraft = draft.Intents.FirstOrDefault(i =>
            string.Equals(i.Name, agentID, StringComparison.OrdinalIgnoreCase));

        // An agent without its intent (or the reverse) is a defect that validation reports, so no section of it is opened.
        if (agentDraft is null || intentDraft is null)
            return false;

        // The correction is an interview of one entry that remembers where the agent stood in the domain.
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

        // The map holds this one entry, since the interview ends when the agent is back in the domain.
        interviewState.Map.Add(intentDraft);

        // The fingerprint lets leaving the edit tell a rewrite from a look.
        interviewState.Revision!.Was = Fingerprint(interviewState);

        // The agent leaves the domain for the length of the edit, so letting it back in cannot write it twice.
        draft.Agents.Remove(agentDraft);
        draft.Intents.Remove(intentDraft);
        // The Draft without the agent is saved, so a closed tab mid-edit loses nothing.
        draftStateService.Set(draft);

        // The correction becomes the interview the page shows.
        Current = interviewState;

        // The edit opens at the Target whichever section needs fixing, so every section after a change is rechecked.
        await EnterPassAsync(interviewState, InterviewStep.AgentTarget, cancellationToken, revisiting: true);

        // The agent is open for correction.
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReopenAsync(int entry, CancellationToken cancellationToken = default)
    {
        // A repeated gesture is reported as handled, since the turn it repeats is still running.
        if (!EnterTurn())
            return true;

        try
        {
            // The correction opens with the claim held.
            return await ReopenedAsync(entry, cancellationToken);
        }
        finally
        {
            // The claim is released even when the opening fails.
            LeaveTurn();
        }
    }

    /// <summary>
    /// Opens one agent of today's map for correction, with the turn already claimed.
    /// </summary>
    private async Task<bool> ReopenedAsync(int entry, CancellationToken cancellationToken)
    {
        // One correction at a time and never over the map, which is settled against itself alone.
        if (Current is not { Resume: null, Revision: null } frontier
            || frontier.Pass == InterviewStep.DomainMapper
            || entry < 0 || entry >= frontier.Map.Count || entry > frontier.At)
            return false;

        // The entry the client picked on the map of today's interview.
        IntentDraft intentDraft = frontier.Map[entry];
        AgentDraft? agentDraft;
        AgentRevision revision;

        // The agent being written now is the first of two cases.
        if (frontier.OnAnEntry && entry == frontier.At)
        {
            // The agent in hand is in no list of the domain: nothing leaves the Draft.
            agentDraft = frontier.Agent;
            revision = new AgentRevision { Origin = agentDraft.Origin };
        }
        // An agent already let into the domain is the second case.
        else
        {
            // Without a Draft there is no domain to take the agent from.
            if (draftStateService.Current is not { } draft)
                return false;

            // The agent and its intent are found in the domain by the entry's name.
            agentDraft = draft.Agents.FirstOrDefault(a =>
                string.Equals(a.ID, intentDraft.Name, StringComparison.OrdinalIgnoreCase));

            IntentDraft? domainIntent = draft.Intents.FirstOrDefault(i =>
                string.Equals(i.Name, intentDraft.Name, StringComparison.OrdinalIgnoreCase));

            // An agent without its intent (or the reverse) is a defect that validation reports.
            if (agentDraft is null || domainIntent is null)
                return false;

            // Out of the domain for the length of the correction, so accepting it cannot write it twice.
            revision = new AgentRevision
            {
                IntentAt = draft.Intents.IndexOf(domainIntent),
                AgentAt = draft.Agents.IndexOf(agentDraft),
                Origin = agentDraft.Origin
            };

            draft.Agents.Remove(agentDraft);
            draft.Intents.Remove(domainIntent);

            // The Draft without the agent is saved, so a closed tab mid-correction loses nothing.
            draftStateService.Set(draft);

            // The correction works on the domain's own intent, which the acceptance puts back.
            intentDraft = domainIntent;
        }

        // The correction is an interview of one entry that keeps the frontier it set aside.
        InterviewState correction = new InterviewState
        {
            At = 0,
            Agent = agentDraft,
            Revision = revision,
            Resume = frontier
        };

        // The map holds this one entry, since the correction ends when the agent is back.
        correction.Map.Add(intentDraft);

        // The fingerprint lets leaving the correction tell a rewrite from a look.
        revision.Was = Fingerprint(correction);

        // The correction becomes the interview the page shows.
        Current = correction;

        // The correction opens at the Target whichever section needs fixing, so every section after a change is rechecked.
        await EnterPassAsync(correction, InterviewStep.AgentTarget, cancellationToken, revisiting: true);

        // The agent is open for correction.
        return true;
    }

    /// <summary>
    /// Hands the interview back to the frontier a correction set aside.
    /// </summary>
    private async Task ResumeAsync(InterviewState correction, InterviewState frontier, CancellationToken cancellationToken)
    {
        // The agent in hand carries what the correction wrote; a resumed save file holds two copies of it.
        if (HoldsInHand(correction, frontier))
            frontier.Agent = correction.Agent;

        // The answer box is primed once per exchange, so the count must never run backwards.
        frontier.Exchanges = Math.Max(frontier.Exchanges, correction.Exchanges);

        // The set-aside interview becomes the one the page shows again.
        Current = frontier;

        // An agent that was waiting for the client's word waits again: the correction rechecked everything after it.
        if (frontier is { Pass: InterviewStep.AgentFormatting, ReadyForReview: true })
        {
            // The save file is brought up to date and the client is asked again.
            Keep(frontier);
            return;
        }

        // Any other pass is re-entered as a revisit, so it reads the corrected agent as settled fact.
        await EnterPassAsync(frontier, frontier.Pass, cancellationToken, revisiting: true);
    }

    /// <summary>
    /// Whether the agent under correction is the one the set-aside interview holds in hand.
    /// </summary>
    private static bool HoldsInHand(InterviewState correction, InterviewState frontier) =>
        // Both interviews name the same intent, compared as the framework compares intents.
        frontier.OnAnEntry
        && string.Equals(frontier.Intent.Name, correction.Intent.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The step the set-aside interview stood on, acceptance counted as the one after Formatting.
    /// </summary>
    private static int StandsAt(InterviewState frontier) =>
        // Acceptance counts as the step after Formatting; any other pass stands at its own number.
        frontier is { Pass: InterviewStep.AgentFormatting, ReadyForReview: true }
            ? (int)InterviewStep.AgentFormatting + 1
            : (int)frontier.Pass;

    /// <inheritdoc />
    public async Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default)
    {
        // A turn already running owns the interview: the caller gets the state as it stands.
        if (!EnterTurn())
            return Current ?? new InterviewState();

        try
        {
            // The answer is carried through with the claim held.
            return await AnsweredAsync(answer, cancellationToken);
        }
        finally
        {
            // The claim is released even when the turn fails.
            LeaveTurn();
        }
    }

    /// <summary>
    /// One answer, carried through every pass it settles.
    /// </summary>
    private async Task<InterviewState> AnsweredAsync(string answer, CancellationToken cancellationToken)
    {
        // An answer arriving before any opening starts the interview first.
        InterviewState interviewState = Current ?? await StartCoreAsync(cancellationToken);

        // A blank answer changes nothing and costs no model call.
        if (string.IsNullOrWhiteSpace(answer))
            return interviewState;

        // The client's answer goes to the pass in progress, whose tools write what can be written.
        await ExchangeAsync(interviewState, answer, cancellationToken);

        // Once the state machine confirms the pass settled the next one opens in the same turn, so the client never acknowledges a pass boundary.
        // Each new pass must confirm for itself; a pass that settles the moment it opens (a toolkit already described in full) chains on.
        // The loop stops at Formatting: letting the agent into the domain is the client's decision.
        // An edit and a fresh composition chain the same way, so a change to one section rechecks every section after it.
        while (interviewState is { ReadyForReview: true, Error: null, Pass: not InterviewStep.AgentFormatting })
        {
            // A correction of the agent in hand stops where the interview stood, since beyond it the agent was never written.
            if (interviewState.Resume is { } frontier
                && HoldsInHand(interviewState, frontier)
                && (int)interviewState.Pass + 1 >= StandsAt(frontier))
            {
                // The set-aside interview takes over again.
                await ResumeAsync(interviewState, frontier, cancellationToken);
                return Current!;
            }

            // Leaving the map steps onto its first entry, since the map settles no agent for the next pass to be about.
            if (interviewState.Pass == InterviewStep.DomainMapper)
                interviewState.At = 0;

            // The next pass opens and asks its first question.
            await EnterPassAsync(interviewState, interviewState.Pass + 1, cancellationToken);
        }

        // The state carries the question the client answers next.
        return interviewState;
    }

    /// <inheritdoc />
    public async Task<bool> AcceptAsync(CancellationToken cancellationToken = default)
    {
        // A repeated gesture is reported as handled, since the turn it repeats is still running.
        if (!EnterTurn())
            return true;

        try
        {
            // The acceptance runs with the claim held.
            return await AcceptedAsync(cancellationToken);
        }
        finally
        {
            // The claim is released even when the acceptance fails.
            LeaveTurn();
        }
    }

    /// <summary>
    /// Lets the agent in hand into the domain, with the turn already claimed.
    /// </summary>
    private async Task<bool> AcceptedAsync(CancellationToken cancellationToken)
    {
        // Without an interview there is nothing to let in.
        if (Current is not { } interviewState)
            return false;

        // The agent in hand never left the interview it was reopened from, so it goes back there instead of into the domain.
        if (interviewState.Resume is { } resumption && HoldsInHand(interviewState, resumption))
        {
            // The set-aside interview takes over again.
            await ResumeAsync(interviewState, resumption, cancellationToken);
            return true;
        }

        // The domain the agent joins; a first acceptance starts it.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // The revision is present only when the agent came out of the domain to be corrected.
        AgentRevision? revision = interviewState.Revision;

        // The provenance records whether the client's upload, their edit or Alembic wrote this agent.
        Provenance origin = Committed(revision, interviewState);

        // The intent and its agent are stamped together so the migration report names them alike.
        interviewState.Intent.Origin = origin;
        interviewState.Agent.Origin = origin;

        // A fresh agent takes the intent's name; one from the client's configuration keeps its own ID, since an edit must not recase it.
        if (string.IsNullOrWhiteSpace(interviewState.Agent.ID))
            interviewState.Agent.ID = interviewState.Intent.Name;

        // An edited agent keeps the C# facts it arrived with, since a save file and an archive carry the real names and tier.
        if (revision is null)
        {
            // The class names below are proposals from the naming convention, flagged for the client to confirm.
            interviewState.Agent.Code.Inferred = true;
            interviewState.Agent.Code.AgentClassName = AgentCodeFacts.ProposeClassName(interviewState.Intent.Name, "Agent");

            // An agent with no native tools gets no tool class: an MCP-only agent's tools arrive at runtime.
            interviewState.Agent.Code.ToolClassName = interviewState.Agent.Tools.Count > 0
                ? AgentCodeFacts.ProposeClassName(interviewState.Intent.Name, "Tool")
                : null;
        }
        else if (interviewState.Agent.Tools.Count > 0 && interviewState.Agent.Code.ToolClassName is null)
        {
            // An agent that gained its first native tools in the edit needs a tool class to emit them into; nothing else of the record changes.
            interviewState.Agent.Code.ToolClassName = AgentCodeFacts.ProposeClassName(interviewState.Intent.Name, "Tool");
        }

        // The intent and the agent return to the places they held in the client's lists; a new one joins the end.
        Restore(draft.Intents, revision?.IntentAt ?? -1, interviewState.Intent);
        Restore(draft.Agents, revision?.AgentAt ?? -1, interviewState.Agent);

        // An agent corrected behind the frontier is back in its place and the interview resumes there.
        if (interviewState.Resume is { } frontier)
        {
            // The Draft with the agent back in it is saved before the frontier resumes.
            draftStateService.Set(draft);
            await ResumeAsync(interviewState, frontier, cancellationToken);
            return true;
        }

        // The sitting names the next entry while the map has one; after the last entry there is no interview left to resume.
        draft.Sitting = interviewState.At + 1 < interviewState.Map.Count
            ? new InterviewSitting
            {
                Pass = InterviewStep.AgentTarget,
                At = interviewState.At + 1,
                Map = [.. interviewState.Map],
                Agent = new AgentDraft()
            }
            : null;

        // The Draft is saved so the page and the save file show the agent as part of the domain.
        draftStateService.Set(draft);

        // The map is the client's list of what their business gets asked, so the interview walks on to the next entry.
        if (interviewState.At + 1 < interviewState.Map.Count)
        {
            // The next entry starts with a blank agent.
            interviewState.At++;
            interviewState.Agent = new AgentDraft();

            // The next agent opens at its Target.
            await EnterPassAsync(interviewState, InterviewStep.AgentTarget, cancellationToken);
            return true;
        }

        // The map is spent and the interview with it; colleagues are settled afterwards on the Collaboration form.
        Abandon();
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> BackAsync(CancellationToken cancellationToken = default)
    {
        // A repeated gesture is reported as handled, since the turn it repeats is still running.
        if (!EnterTurn())
            return true;

        try
        {
            // The step back runs with the claim held.
            return await SteppedBackAsync(cancellationToken);
        }
        finally
        {
            // The claim is released even when the step back fails.
            LeaveTurn();
        }
    }

    /// <summary>
    /// Steps back into the previous part of the interview, with the turn already claimed.
    /// </summary>
    private async Task<bool> SteppedBackAsync(CancellationToken cancellationToken)
    {
        // Without an interview there is nothing to step back in.
        if (Current is not { } interviewState)
            return false;

        // An agent waiting to be let in steps back into Formatting, the step it waits on, since the client wants to change a line they are reading.
        if (interviewState is { ReadyForReview: true, Pass: InterviewStep.AgentFormatting })
        {
            // Formatting reopens as a revisit of what is written.
            await EnterPassAsync(interviewState, interviewState.Pass, cancellationToken, revisiting: true);
            return true;
        }

        // Every pass other than those below goes back to the pass before it.
        switch (interviewState.Pass)
        {
            // The map is the first step; the landing behind it is not a step.
            case InterviewStep.DomainMapper:
                return false;

            // An edit opened from the walk has no step before its Target; the walk's own way back leaves it.
            case InterviewStep.AgentTarget when interviewState.Revision is not null:
                return false;

            // Out of the first entry is back onto the map, whose entries survive for the mapper to read back.
            case InterviewStep.AgentTarget when interviewState.At == 0:
                interviewState.At = -1;
                await EnterPassAsync(interviewState, InterviewStep.DomainMapper, cancellationToken, revisiting: true);
                return true;

            // Out of an entry into the one before it, which is in the domain already and comes back into the client's hands.
            case InterviewStep.AgentTarget:
                interviewState.At--;
                Uncommit(interviewState);
                await EnterPassAsync(interviewState, InterviewStep.AgentFormatting, cancellationToken, revisiting: true);
                return true;

            // Any later pass steps back onto the pass before it.
            default:
                await EnterPassAsync(interviewState, interviewState.Pass - 1, cancellationToken, revisiting: true);
                return true;
        }
    }

    /// <summary>
    /// The last message of the turn that asks the client something, which is the question shown on screen.
    /// </summary>
    /// <remarks>A turn that asks nothing falls back to the last thing said, so the defect is visible.</remarks>
    private static string LastAsked(AgentResponse response)
    {
        // Only the assistant's own non-blank messages count, since a run holds several around its tool calls.
        string[] texts = [.. response.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)];

        // The question mark marks a message that asks, so narration after the question is skipped.
        string? question = texts.LastOrDefault(t => t.Contains('?'));

        // The question is shown; a turn without one shows its last message or the joined text.
        return question ?? texts.LastOrDefault() ?? response.Text?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Writes where the interview stands into the Draft, so the save file is never out of date.
    /// </summary>
    /// <remarks>It runs every turn, so the download beside the question is true whenever the client takes it.</remarks>
    private void Keep(InterviewState interviewState)
    {
        // The domain the sitting is saved into; a first save starts it.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // The sitting carries the interview in hand and the one a correction set aside.
        draft.Sitting = SittingOf(interviewState);
        draft.Sitting.Resume = interviewState.Resume is { } frontier ? SittingOf(frontier) : null;

        // The Draft is saved so the save file never trails the screen.
        draftStateService.Set(draft);
    }

    /// <summary>
    /// The facts of one interview that have to survive a closed tab.
    /// </summary>
    private static InterviewSitting SittingOf(InterviewState interviewState) => new()
    {
        Pass = interviewState.Pass,
        At = interviewState.At,
        Map = [.. interviewState.Map],
        Agent = interviewState.Agent,
        Example = interviewState.Example,
        Revision = interviewState.Revision
    };

    /// <summary>
    /// An interview set aside, rebuilt from its sitting without asking the model anything.
    /// </summary>
    /// <remarks>It is entered only when a correction hands it back.</remarks>
    private static InterviewState StateOf(InterviewSitting sitting)
    {
        // The scalar facts of the sitting come back as saved.
        InterviewState interviewState = new InterviewState
        {
            Pass = sitting.Pass,
            At = sitting.At,
            Agent = sitting.Agent,
            Example = sitting.Example,
            Revision = sitting.Revision
        };

        // The map is read-only on the state, so its entries are added one by one.
        interviewState.Map.AddRange(sitting.Map);

        // The rebuilt interview waits for a correction to hand it back.
        return interviewState;
    }

    /// <summary>
    /// Takes the entry the interview has just stepped back onto out of the Draft and back into hand.
    /// </summary>
    private void Uncommit(InterviewState interviewState)
    {
        // Without a Draft nothing was ever let into the domain.
        if (draftStateService.Current is not { } draft)
            return;

        // The agent of the entry stepped back onto, matched by the intent's name.
        AgentDraft? domainAgent = draft.Agents.FirstOrDefault(a =>
            string.Equals(a.ID, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase));

        // The agent leaves the domain and returns to the client's hands, so letting it in again cannot write it twice.
        if (domainAgent is not null)
        {
            draft.Agents.Remove(domainAgent);
            interviewState.Agent = domainAgent;
        }

        // The intent leaves the domain with its agent.
        draft.Intents.RemoveAll(i =>
            string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase));

        // The Draft without the entry is saved.
        draftStateService.Set(draft);
    }

    /// <inheritdoc />
    public void Abandon()
    {
        // The interview, its agent and its session are dropped together, so nothing of the old conversation survives.
        Current = null;
        agent = null;
        session = null;
    }

    /// <summary>
    /// Moves the interview into a pass: new agent, new session and the pass's opening question.
    /// </summary>
    /// <remarks>Only the configuration crosses the boundary, read as settled fact through <c>GetAgentSoFar</c> and <c>GetToolkit</c>.</remarks>
    private async Task EnterPassAsync(
        InterviewState interviewState,
        InterviewStep interviewPass,
        CancellationToken cancellationToken,
        bool revisiting = false)
    {
        // The pass opens unsettled; the exchange count marks where it begins.
        interviewState.Pass = interviewPass;
        interviewState.ReadyForReview = false;
        interviewState.PassOpenedAt = interviewState.Exchanges;

        // The agent is about its intent from its first pass, so mid-interview checks never report it as unhandled; an ID from the client's own configuration is kept.
        if (interviewState.OnAnEntry && string.IsNullOrWhiteSpace(interviewState.Agent.ID))
            interviewState.Agent.ID = interviewState.Intent.Name;

        // An agent that already exists is corrected however the pass was reached, whether reopened directly or chained into from an earlier pass.
        bool correcting = interviewState.Revision is not null;

        // The pass gets its own agent and session with the tools it declares.
        await BuildAgentAsync(interviewState, interviewPass, correcting);

        // The opening question of the interview is fixed text, since there is nothing yet for a model to read back; the session built above receives the client's first answer.
        if (interviewPass == InterviewStep.DomainMapper && !revisiting)
        {
            // The screen shows the fixed question with no offers under it.
            interviewState.Question = Said("OpeningQuestion");
            interviewState.Choice = null;
            interviewState.Example = null;
            interviewState.Placing = null;
            interviewState.Quoted = null;
            interviewState.Traits = [];
            interviewState.Chosen = false;
            interviewState.Exchanges++;

            // The Draft is saved while skipping the model, so the save file is true from the first question on screen.
            Keep(interviewState);
            return;
        }

        // Every other pass opens with a message from the state machine; the model answers with its first question.
        await ExchangeAsync(interviewState, Bootstrap(interviewState, interviewPass, correcting), cancellationToken);
    }

    /// <summary>
    /// Fetches a text of the <c>Alembic</c> prompt's <c>PromptInjections</c> that a pass opens with.
    /// </summary>
    private string Said(string injectionName, params (string Placeholder, string Value)[] values) =>
        // The text lives in alembic.json and the placeholders are filled with the facts of this entry.
        alembicPromptService.ResolvePromptInjection(AlembicPromptId, injectionName, values);

    /// <summary>
    /// What a pass says about what it settles, from the <c>Opening</c> injection of its own prompt.
    /// </summary>
    /// <remarks>It is never shown to the client: the model needs a user turn to answer.</remarks>
    private string Opening(InterviewStep interviewPass) =>
        // The pass's own prompt holds the sentence that names what it settles.
        alembicPromptService.ResolvePromptInjection(interviewPass.ToString(), "Opening");

    /// <summary>
    /// What the agent is sent to open a pass.
    /// </summary>
    /// <remarks>Where the pass stands goes in the opening message, so it never spends a tool call or a guess working it out.</remarks>
    private string Bootstrap(InterviewState interviewState, InterviewStep interviewPass, bool correcting)
    {
        // The map pass has no entry to place: it opens with its own sentence and what is known of the trade.
        if (interviewPass == InterviewStep.DomainMapper)
            return Opening(interviewPass) + Known(interviewState);

        // Which job the pass is doing is stated as a fact read off the agent; what to do about each job is the shared layer's rule.
        string status = correcting
            ? Reads(interviewState.Agent)
            : Stands(interviewState.Agent);

        // The message places the entry, states the agent's condition and names what the pass settles, with the trade known so far.
        return Said("EntryStanding",
                   ("entry", (interviewState.At + 1).ToString(CultureInfo.InvariantCulture)),
                   ("total", interviewState.Map.Count.ToString(CultureInfo.InvariantCulture)),
                   ("intent", interviewState.Intent.Name ?? string.Empty),
                   ("description", interviewState.Intent.Description ?? string.Empty))
               + " " + status + " "
               + Opening(interviewPass)
               + Known(interviewState);
    }

    /// <summary>
    /// What the client has already said about their work, handed to a step before it asks anything.
    /// </summary>
    /// <remarks>The facts are material to build questions on and never prose to copy into a section.</remarks>
    private string Known(InterviewState interviewState)
    {
        // What the client said about the business as a whole.
        List<KnownFact> trade = [.. (draftStateService.Current?.Learned ?? [])];

        // What the client said about the agent in hand; the map holds an agent nobody writes, so it contributes none.
        List<KnownFact> agent = interviewState.OnAnEntry ? [.. interviewState.Agent.Known] : [];

        // The other agents by subject alone.
        List<string> neighbours = Neighbours(interviewState);

        // A pass that knows nothing about the trade adds no block.
        if (trade.Count == 0 && agent.Count == 0 && neighbours.Count == 0)
            return string.Empty;

        // The block opens with its heading.
        string header = " " + Said("KnownHeader");

        // The business comes first.
        if (trade.Count > 0)
            header += "\n" + Said("KnownBusiness") + " " + Facts(trade);

        // The agent in hand comes last and nearest the question, since the step is about it.
        if (agent.Count > 0)
            header += "\n" + Said("KnownAgent") + " " + Facts(agent);

        // The other agents are listed by subject, since their sentences are fetched through RecallAgent only when one bears on the question.
        if (neighbours.Count > 0)
            header += "\n" + Said("KnownNeighbours") + " " + string.Join(" ", neighbours);

        // A single note at the foot says that inferred facts are Alembic's reading of an upload and not the client's word.
        if (trade.Concat(agent).Any(fact => fact.Inferred))
            header += "\n" + Said("KnownInferredNote");

        // The block is appended to the pass's opening message.
        return header;
    }

    /// <summary>
    /// A run of facts as a step reads them, each saying whether anybody actually said it.
    /// </summary>
    private string Facts(IEnumerable<KnownFact> facts) =>
        // One line per fact, with the inferred ones marked so the step offers them for correction.
        string.Join(" ", facts.Select(fact =>
            $"- {fact.Subject}: {fact.Fact}" + (fact.Inferred ? " " + Said("FactInferred") : string.Empty)));

    /// <summary>
    /// What is known about every other agent of the domain, named one by one.
    /// </summary>
    /// <remarks>It covers the written agents and the entries still ahead on the map, since a step that sees only its own agent claims ground twice.</remarks>
    private List<string> Neighbours(InterviewState interviewState)
    {
        // The domain holding the agents already let in.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // The facts of each written agent, looked up by intent name as the framework matches intents.
        Dictionary<string, List<KnownFact>> factsByAgent = draft.Agents
            .Where(agent => agent.ID is not null)
            .ToDictionary(agent => agent.ID!, agent => agent.Known, StringComparer.OrdinalIgnoreCase);

        // The names of the other agents of the domain and the map, offered to the step as subjects it may recall.
        List<string> neighbours = [];

        // Intents of the domain come first and the map's entries after them.
        foreach (IntentDraft intent in draft.Intents.Concat(interviewState.Map))
        {
            // An unnamed entry, the agent in hand and an entry listed already (the map repeats accepted intents) are skipped.
            if (intent.Name is not { Length: > 0 } name
                || string.Equals(name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase)
                || neighbours.Any(line => line.StartsWith($"- {name}:", StringComparison.OrdinalIgnoreCase)))
                continue;

            // An agent with facts is listed by their subjects; one without falls back to its routing description.
            string topics = factsByAgent.TryGetValue(name, out List<KnownFact>? facts) && facts.Count > 0
                ? string.Join(", ", facts.Select(fact => fact.Subject).Distinct(StringComparer.OrdinalIgnoreCase))
                : intent.Description ?? Said("NeighbourUnknown");

            // One line per neighbour, named and with the subjects it answers for.
            neighbours.Add($"- {name}: {topics}");
        }

        // The neighbours go to the opening message by subject alone.
        return neighbours;
    }

    /// <summary>
    /// The agent as it stands, whole, handed to a step that is reopening it.
    /// </summary>
    /// <remarks>The whole agent arrives in the opening message, since correcting one section over prose that speaks about another is the usual way a revision goes wrong.</remarks>
    private string Reads(AgentDraft agent)
    {
        List<string> sections = [];

        // Each section already written is read back in full, in the order the interview writes them.
        if (AgentRows.Plain(agent.Target) is { Length: > 0 } target)
            sections.Add($"Target: {target}");

        // The voice follows the Target since it was settled right after it.
        if (AgentRows.Plain(agent.Personality) is { Length: > 0 } personality)
            sections.Add($"Personality: {personality}");

        // The toolkit is listed by tool name, since GetToolkit returns the contracts.
        if (agent.Tools.Count > 0)
            sections.Add($"Toolkit: {string.Join(", ", agent.Tools.Select(tool => tool.Name ?? "(unnamed)"))}");

        // The Instructions come after the toolkit they refer to.
        if (AgentRows.Plain(agent.Instructions) is { Length: > 0 } instructions)
            sections.Add($"Instructions: {instructions}");

        // The Formatting closes the agent.
        if (AgentRows.Plain(agent.Formatting) is { Length: > 0 } formatting)
            sections.Add($"Formatting: {formatting}");

        // A sitting saved mid-first-answer has nothing to read back, so the ordinary sentence about what stands is the true one.
        if (sections.Count == 0)
            return Stands(agent);

        // The sentence is the one the shared layer teaches every pass to recognise, so its wording stays exact.
        return Said("ReadsAgent", ("sections", string.Join(" | ", sections)));
    }

    /// <summary>
    /// What is already written on the agent the step is opening on.
    /// </summary>
    /// <remarks>It is read off the agent and not off the running pass; a missing Toolkit is not reported as unwritten since an MCP-only agent is legal.</remarks>
    private string Stands(AgentDraft agent)
    {
        List<string> sections = [];

        // Each section already written adds its sentence, so a pass entered over a part-filled agent never opens as if blank.
        if (!string.IsNullOrWhiteSpace(agent.Target))
            sections.Add(Said("StandsTarget"));

        // The voice is written.
        if (!string.IsNullOrWhiteSpace(agent.Personality))
            sections.Add(Said("StandsPersonality"));

        // At least one tool is declared.
        if (agent.Tools.Count > 0)
            sections.Add(Said("StandsToolkit"));

        // The Instructions are written.
        if (!string.IsNullOrWhiteSpace(agent.Instructions))
            sections.Add(Said("StandsInstructions"));

        // The Formatting is written.
        if (!string.IsNullOrWhiteSpace(agent.Formatting))
            sections.Add(Said("StandsFormatting"));

        // An agent with nothing written is stated blank; otherwise the sentence lists what is already written.
        return sections.Count == 0
            ? Said("StandsBlank")
            : Said("StandsPartial", ("written", string.Join(", ", sections)));
    }

    /// <summary>
    /// Assembles the agent for one pass: its prompt and only the tools that pass is allowed.
    /// </summary>
    /// <remarks>The job (composing or correcting) goes to the prompt composer and not into the toolset, since both jobs use the same tools.</remarks>
    private async Task BuildAgentAsync(InterviewState interviewState, InterviewStep interviewPass, bool correcting)
    {
        // The pass's name identifies the agent and selects its prompt in alembic.json.
        string interviewerId = interviewPass.ToString();

        // The tools write into this interview's state and read the Draft, the checks and the recap.
        InterviewTools tools = new InterviewTools(interviewState, draftStateService, draftValidationService, recapService, alembicPromptService);

        // The pass is offered the tools its step names and nothing else, so what it may write is settled by which tools exist.
        MorganaToolAdapter toolAdapter = alembicPromptService.OfferTools(InterviewSteps.Of(interviewPass), tools);

        // Writing dispositive prose that does not contradict itself needs the Efficiency tier, whatever tier the domain's agents run on.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        // The pass's agent is built over that client with the composed prompt and the offered tools.
        agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = $"alembic-{interviewerId.ToLowerInvariant()}",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                // The output ceiling is the one Alembic sets for its own calls.
                MaxOutputTokens = CodeEmitService.OutputCeiling,

                // The prompt carries Morgana's voice and Alembic's prose for the pass, in the job (composing or correcting) it is doing.
                Instructions = await alembicPromptService.ComposeAsync(interviewerId, correcting),

                // Each tool is logged with its call and answer, since a refusal reaches the model alone and the client may be told the step was settled.
                Tools = [.. (await toolAdapter.CreateAllFunctionsAsync())
                                              .Select(function => new LoggedTool(function, logger, interviewerId))]
            }
        });

        // The pass starts a fresh session, so it holds no history of earlier passes.
        session = await agent.CreateSessionAsync();
    }

    /// <summary>
    /// One round trip: the client's words in, Alembic's words out and whatever its tools wrote along the way already in the state.
    /// </summary>
    private async Task ExchangeAsync(InterviewState interviewState, string message, CancellationToken cancellationToken)
    {
        // An interview with no pass entered has no agent to ask.
        if (agent is null || session is null)
            return;

        // Nothing of the last turn's error or offers may survive into this one.
        interviewState.OpenTurn();

        // The snapshot before the turn is what the panel's highlighted rows are diffed against.
        Dictionary<string, string?> baseline = interviewState.Snapshot();

        // The turn's start, from which its duration is reported when it is given up on.
        long startedAt = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "{Pass} is answering {Length} characters from the client",
            interviewState.Pass.ToString(), message.Length);

        // The turn ends when the client leaves or when the ceiling passes, whichever comes first.
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(turnCeiling);

        try
        {
            // The model answers the client's message and calls the pass's tools, which write into the state.
            AgentResponse response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, message), session, cancellationToken: bounded.Token);

            logger.LogInformation(
                "{Pass} answered in {Elapsed:0.0}s",
                interviewState.Pass.ToString(), Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            // A field whose value moved this turn is lit in the panel; a tool called with an unchanged value lights nothing.
            foreach ((string field, string? value) in interviewState.Snapshot())
                if (!string.Equals(value, baseline[field], StringComparison.Ordinal))
                    interviewState.Changed.Add(field);

            // The question the client is shown is the last message of the turn that asks something.
            string question = LastAsked(response);

            // A turn spent entirely on tool calls has nothing to say, so the question on screen stands.
            if (question.Length > 0)
            {
                interviewState.Question = question;
                interviewState.Exchanges++;
            }

            // The offers are promoted even when the turn wrote no question, since a turn may offer a button for the question already on screen.
            interviewState.ShowTurnOffers();
        }
        // The ceiling passed while the client was still there: the turn is given up on and the client sends the answer again.
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "{Pass} was still waiting after {Elapsed:0.0}s and was given up on",
                interviewState.Pass.ToString(), Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            // The client is told the turn was dropped and that sending the answer again is safe.
            interviewState.Error = "That turn took too long and was given up on. Your work is safe — send the answer again.";
        }
        // Neither the ceiling nor the client stopped the turn: a call beneath it gave up, which the log must not blame on the ceiling.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "{Pass} was cancelled beneath the turn after {Elapsed:0.0}s, inside its ceiling",
                interviewState.Pass.ToString(), Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            // The client is told that the model gave up and that sending the answer again is safe.
            interviewState.Error = "The model stopped answering before the turn was done. Your work is safe — send the answer again.";
        }
        // Any other failure is shown to the client with its reason; the work already written stays.
        catch (Exception ex)
        {
            logger.LogError(ex, "The interview turn failed after {Elapsed:0.0}s", Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
            interviewState.Error = $"The turn could not be completed: {ex.Message}";
        }

        // The save file is brought up to date after every turn (the opening one and a failed one included), so the download is true whenever it is taken.
        Keep(interviewState);
    }

    /// <summary>
    /// What an element's provenance becomes when the client lets it into the domain.
    /// </summary>
    /// <remarks>Only an edit that changed an imported agent makes it Revised, so the migration report names exactly the agents the client touched.</remarks>
    private static Provenance Committed(AgentRevision? revision, InterviewState interviewState)
    {
        // An agent written in this interview exists in no uploaded file.
        if (revision is null)
            return Provenance.Authored;

        // An agent read on the way past goes back under the provenance it left with, since reading is not touching.
        if (revision.Was == Fingerprint(interviewState))
            return revision.Origin;

        // A rewritten agent stays Authored when it was written today and becomes Revised when it was imported.
        return revision.Origin == Provenance.Authored ? Provenance.Authored : Provenance.Revised;
    }

    /// <summary>
    /// Every field the interview can write, as one string, so two moments of the same agent can be
    /// compared without asking each field in turn.
    /// </summary>
    private static string Fingerprint(InterviewState interviewState) =>
        // Fields are sorted by key so equal agents always produce equal text.
        string.Join("\0", interviewState.Snapshot()
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => $"{field.Key}={field.Value}"));

    /// <summary>
    /// Puts an element back where the edit took it from, or on the end when it was never there.
    /// </summary>
    private static void Restore<T>(List<T> list, int at, T element)
    {
        // The client's order is kept: an element that came out of the list returns to its place.
        if (at >= 0 && at <= list.Count)
            list.Insert(at, element);
        // An element that was never in the list joins the end.
        else
            list.Add(element);
    }

    /// <summary>
    /// One tool of the interview, with the call and the sentence it is answered with written down.
    /// </summary>
    /// <remarks>A refusal reaches the model alone, so the log is the only place that shows a pass told the client something its own tool refused.</remarks>
    private sealed class LoggedTool : DelegatingAIFunction
    {
        /// <summary>How many characters of one argument identify a call.</summary>
        private const int Kept = 160;

        /// <summary>The log that records each call and the sentence it was answered with.</summary>
        private readonly ILogger logger;

        /// <summary>Which pass made the call, under the name <c>alembic.json</c> declares it.</summary>
        private readonly string pass;

        /// <summary>
        /// Wraps one tool of a pass so its calls are logged under the pass's name.
        /// </summary>
        public LoggedTool(AIFunction inner, ILogger logger, string pass) : base(inner)
        {
            this.logger = logger;
            this.pass = pass;
        }

        /// <inheritdoc />
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // The clock starts before the call so the log shows how long the tool took.
            long startedAt = Stopwatch.GetTimestamp();

            logger.LogInformation("{Pass} is calling {Tool}({Arguments})", pass, Name, Written(arguments));

            // The tool runs and answers the model as it would unlogged.
            object? answer = await base.InvokeCoreAsync(arguments, cancellationToken);

            // The answer is read back to tell a refusal from a told sentence.
            ToolReply? reply = Read(answer);

            // A refusal is logged as a warning, since a pass can report it to the client as settled while its tool changed nothing.
            logger.Log(
                reply?.Error is null ? LogLevel.Information : LogLevel.Warning,
                "{Pass} called {Tool}({Arguments}) and was {Outcome}, {Elapsed}ms later: {Answer}",
                pass, Name, Written(arguments), reply?.Error is null ? "told" : "refused",
                Stopwatch.GetElapsedTime(startedAt).Milliseconds, Short(reply?.Error ?? reply?.Text ?? answer?.ToString()));

            // The model receives the answer untouched.
            return answer;
        }

        /// <summary>
        /// The record a tool answered with, read back from the compact JSON that the model receives; null where the answer is not one.
        /// </summary>
        private static ToolReply? Read(object? answer)
        {
            // Only a JSON text can be a tool's reply.
            if (answer is not string json)
                return null;

            try
            {
                // The text is the compact JSON of the reply record, cased as the model received it.
                return JsonSerializer.Deserialize<ToolReply>(json, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                // A tool that answered something else leaves nothing to classify; the log shows the raw text.
                return null;
            }
        }

        /// <summary>The call's arguments as one line, each cut to what identifies it.</summary>
        private static string Written(AIFunctionArguments arguments) =>
            // Each argument is shown by name with a short value.
            string.Join(", ", arguments.Select(argument => $"{argument.Key}: {Short(argument.Value?.ToString())}"));

        /// <summary>One value, cut short and on a single line.</summary>
        private static string Short(string? value)
        {
            // A multi-line prose value is flattened so each call stays one log line.
            string text = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();

            // Past the limit only the start is kept, marked as cut.
            return text.Length <= Kept ? text : text[..Kept] + "…";
        }
    }
}
