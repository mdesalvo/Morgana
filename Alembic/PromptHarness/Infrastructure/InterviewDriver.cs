using Distiller.Interfaces;
using Distiller.Model;

namespace PromptHarness.Infrastructure;

/// <summary>
/// One exchange of a driven interview, kept for a failing test's own diagnostics — not asserted on
/// directly by most tests, but printed when one does fail.
/// </summary>
public sealed record DrivenExchange(InterviewStep Pass, string Question, string Answer);

/// <summary>
/// What a driven run leaves behind: the transcript and the state the interview stood at when the
/// driver stopped.
/// </summary>
public sealed record DrivenInterview(InterviewState FinalState, IReadOnlyList<DrivenExchange> Exchanges)
{
    /// <summary>Renders the transcript as a scrollback, for a failing assertion's message.</summary>
    public override string ToString() =>
        string.Join('\n', Exchanges.Select(e => $"[{e.Pass}] Q: {e.Question}\nA: {e.Answer}"));
}

/// <summary>
/// What the scripted client says about one desk of their business, and how the driver recognises
/// that desk when the interview reaches it.
/// </summary>
/// <param name="Recognisers">
/// Words the client's own description of this desk would leave in the intent the mapper wrote for
/// it. The interview names its own intents, so a script keyed by position would hand the events
/// answers to the reservations agent the first time the mapper lists the two the other way round.
/// </param>
/// <param name="Answers">What the client says on this desk, queued per pass.</param>
public sealed record DeskScript(IReadOnlyList<string> Recognisers, Dictionary<InterviewStep, Queue<string>> Answers);

/// <summary>
/// A whole domain's half of the conversation: the map, one script per desk it produces and the
/// closing step where the desks are told which of them may ring which.
/// </summary>
/// <param name="Mapping">What the client says while the map is drawn.</param>
/// <param name="Desks">One script per desk, matched to the entry in hand rather than to its place.</param>
/// <param name="Colleagues">What the client says when the closing step reads the edges back.</param>
public sealed record DomainScript(
    Queue<string> Mapping,
    IReadOnlyList<DeskScript> Desks,
    Queue<string> Colleagues);

/// <summary>
/// Drives a real <see cref="IInterviewService"/> with a scripted "domain expert" — the same
/// technique a client's own typing would produce, formalised so a test can replay it and assert on
/// where Alembic's own process landed.
/// </summary>
/// <remarks>
/// This is deliberately not a mock of the interview: every exchange is a live call through
/// <see cref="IInterviewService.AnswerAsync"/>, on Alembic's own Performance tier. What is scripted
/// is only the client's half of the conversation — Alembic's own conducting is exactly what is
/// under test and stubbing it would test nothing.
/// </remarks>
public static class InterviewDriver
{
    /// <summary>
    /// Drives the interview until the domain map itself is settled — <see cref="InterviewState.Pass"/>
    /// moves off <see cref="InterviewStep.DomainMapper"/> — and stops there, before any agent exists.
    /// </summary>
    /// <param name="interview">A fresh <see cref="IInterviewService"/>, not yet started.</param>
    /// <param name="mappingScript">What the domain expert says while the map is drawn, in order.</param>
    /// <param name="maxExchanges">A guard against a script that never settles the pass, not a budget.</param>
    public static async Task<DrivenInterview> RunMappingOnlyAsync(
        IInterviewService interview,
        IReadOnlyList<string> mappingScript,
        int maxExchanges = 20,
        CancellationToken cancellationToken = default)
    {
        List<DrivenExchange> exchanges = [];
        InterviewState state = await interview.StartAsync(cancellationToken);
        int scriptIndex = 0;

        for (int i = 0; i < maxExchanges && state.Pass == InterviewStep.DomainMapper; i++)
        {
            string answer = scriptIndex < mappingScript.Count
                ? mappingScript[scriptIndex++]
                : "That's everything, thank you.";

            exchanges.Add(new DrivenExchange(state.Pass, state.Question ?? string.Empty, answer));
            state = await interview.AnswerAsync(answer, cancellationToken);

            if (state.Error is not null)
                break;

            // The closing step ends the interview from inside a turn rather than on an AcceptAsync:
            // the colleagues are settled by the client's own agreement and there is no agent left
            // to let into the domain. Answering on past that would start a second interview.
            if (interview.Current is null)
                break;
        }

        return new DrivenInterview(state, exchanges);
    }

    /// <summary>
    /// Drives the interview end to end: the map, then every entry it produced, accepting each
    /// finished agent as it comes and on through the closing step where the colleagues are settled,
    /// until the interview ends or <paramref name="maxExchanges"/> is spent.
    /// </summary>
    /// <param name="interview">A fresh <see cref="IInterviewService"/>, not yet started.</param>
    /// <param name="script">What the domain expert says, per desk and per pass. A pass whose queue runs
    /// out falls back to a bare agreement — the interview doctrine already requires that "adequate
    /// is not complete" and a step never needs more than a couple of turns, so a script this short
    /// running dry is itself a signal worth seeing in the transcript, not a driver bug to paper over.</param>
    /// <param name="maxExchanges">A guard against a run that never terminates, not a budget.</param>
    public static async Task<DrivenInterview> RunFullAsync(
        IInterviewService interview,
        DomainScript script,
        int maxExchanges = 80,
        CancellationToken cancellationToken = default)
    {
        InterviewState state = await interview.StartAsync(cancellationToken);
        return await RunScriptedAsync(interview, state, Speaking(script), maxExchanges, cancellationToken);
    }

    /// <summary>
    /// The queue the client would be answering out of, given where the interview stands.
    /// </summary>
    /// <remarks>
    /// The map and the closing step are the domain's own two passes and each has one queue. Every
    /// pass in between belongs to whichever desk the interview stands on, found by the words the
    /// client used about it rather than by its place on the map: which entry the mapper wrote first
    /// is the mapper's business and a script that assumed an order would silently answer for the
    /// wrong desk on the run it changed its mind.
    /// </remarks>
    private static Func<InterviewState, Queue<string>> Speaking(DomainScript script) => state =>
    {
        if (state.Pass == InterviewStep.DomainMapper)
            return script.Mapping;

        if (state.Pass == InterviewStep.DomainColleagues)
            return script.Colleagues;

        if (script.Desks.Count == 0)
            return new Queue<string>();

        string entry = $"{state.Intent.Name} {state.Intent.Description} {state.Intent.Label}";

        DeskScript desk = script.Desks
            .Select(d => (Desk: d, Hits: d.Recognisers.Count(word => entry.Contains(word, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(match => match.Hits)
            .First()
            .Desk;

        return desk.Answers.TryGetValue(state.Pass, out Queue<string>? queue) ? queue : new Queue<string>();
    };

    /// <summary>
    /// Drives an edit already opened with <see cref="IInterviewService.ReviseAsync"/> the rest of
    /// the way — every section from Target on, following <paramref name="script"/> per pass, until
    /// the agent is accepted back into the domain or <paramref name="maxExchanges"/> is spent.
    /// </summary>
    /// <remarks>
    /// An edit re-enters at Target and chains through every pass exactly the way a fresh compose
    /// does, so this shares <see cref="RunFullAsync"/>'s own loop — the only difference is where the
    /// state comes from: a fresh compose starts it with <c>StartAsync</c>, an edit already has one
    /// from the caller's own <c>ReviseAsync</c>. An edit's map holds exactly one entry, so the single
    /// AcceptAsync this reaches at AgentFormatting always returns <c>false</c> and ends the loop the
    /// same way RunFullAsync's does when the domain map itself runs out — there is no second entry
    /// to carry on to.
    /// </remarks>
    /// <param name="interview">An <see cref="IInterviewService"/> whose <c>ReviseAsync</c> has already opened an agent.</param>
    /// <param name="script">What the domain expert says, queued per pass.</param>
    /// <param name="maxExchanges">A guard against a run that never terminates, not a budget.</param>
    public static Task<DrivenInterview> RunEditAsync(
        IInterviewService interview,
        IReadOnlyDictionary<InterviewStep, Queue<string>> script,
        int maxExchanges = 80,
        CancellationToken cancellationToken = default)
    {
        if (interview.Current is not { } state)
            throw new InvalidOperationException(
                $"{nameof(RunEditAsync)} expects {nameof(IInterviewService.ReviseAsync)} to have already opened the agent.");

        return RunScriptedAsync(
            interview,
            state,
            asked => script.TryGetValue(asked.Pass, out Queue<string>? queue) ? queue : new Queue<string>(),
            maxExchanges,
            cancellationToken);
    }

    private static async Task<DrivenInterview> RunScriptedAsync(
        IInterviewService interview,
        InterviewState state,
        Func<InterviewState, Queue<string>> speaking,
        int maxExchanges,
        CancellationToken cancellationToken)
    {
        List<DrivenExchange> exchanges = [];

        for (int i = 0; i < maxExchanges; i++)
        {
            bool written = state is { Pass: InterviewStep.AgentFormatting, ReadyForReview: true };

            if (written)
            {
                bool more = await interview.AcceptAsync(cancellationToken);
                if (!more)
                    break;

                state = interview.Current!;
                continue;
            }

            Queue<string> queue = speaking(state);
            string answer = queue.Count > 0 ? queue.Dequeue() : "That's right, go ahead.";

            exchanges.Add(new DrivenExchange(state.Pass, state.Question ?? string.Empty, answer));
            state = await interview.AnswerAsync(answer, cancellationToken);

            if (state.Error is not null)
                break;

            // The closing step ends the interview from inside a turn: the agents are all in the
            // domain already, so there is no acceptance left to make and nothing to answer to.
            // Speaking past it does not continue this interview, it starts a second one — which is
            // a live conversation's worth of calls spent on a domain nobody asked for.
            if (interview.Current is null)
                break;
        }

        return new DrivenInterview(state, exchanges);
    }
}
