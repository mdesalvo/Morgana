using Alembic.Interfaces;
using Alembic.Model;

namespace AlembicHarness.Infrastructure;

/// <summary>
/// One exchange of a driven interview, kept for a failing test's own diagnostics — not asserted on
/// directly by most tests, but printed when one does fail.
/// </summary>
public sealed record DrivenExchange(InterviewPass Pass, string Question, string Answer);

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
/// Drives a real <see cref="IInterviewService"/> with a scripted "domain expert" — the same
/// technique a client's own typing would produce, formalised so a test can replay it and assert on
/// where Alembic's own process landed.
/// </summary>
/// <remarks>
/// This is deliberately not a mock of the interview: every exchange is a live call through
/// <see cref="IInterviewService.AnswerAsync"/>, on Alembic's own Performance tier. What is scripted
/// is only the client's half of the conversation — Alembic's own conducting is exactly what is
/// under test, and stubbing it would test nothing.
/// </remarks>
public static class InterviewDriver
{
    /// <summary>
    /// Drives the interview until the domain map itself is settled — <see cref="InterviewState.Pass"/>
    /// moves off <see cref="InterviewPass.DomainMapper"/> — and stops there, before any agent exists.
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

        for (int i = 0; i < maxExchanges && state.Pass == InterviewPass.DomainMapper; i++)
        {
            string answer = scriptIndex < mappingScript.Count
                ? mappingScript[scriptIndex++]
                : "That's everything, thank you.";

            exchanges.Add(new DrivenExchange(state.Pass, state.Question ?? string.Empty, answer));
            state = await interview.AnswerAsync(answer, cancellationToken);

            if (state.Error is not null)
                break;
        }

        return new DrivenInterview(state, exchanges);
    }

    /// <summary>
    /// Drives the interview end to end: the map, then every entry it produced, following
    /// <paramref name="script"/> per pass and accepting each finished agent as it comes, until the
    /// map is exhausted or <paramref name="maxExchanges"/> is spent.
    /// </summary>
    /// <param name="interview">A fresh <see cref="IInterviewService"/>, not yet started.</param>
    /// <param name="script">What the domain expert says, queued per pass. A pass whose queue runs
    /// out falls back to a bare agreement — the interview doctrine already requires that "adequate
    /// is not complete" and a step never needs more than a couple of turns, so a script this short
    /// running dry is itself a signal worth seeing in the transcript, not a driver bug to paper over.</param>
    /// <param name="maxExchanges">A guard against a run that never terminates, not a budget.</param>
    public static async Task<DrivenInterview> RunFullAsync(
        IInterviewService interview,
        IReadOnlyDictionary<InterviewPass, Queue<string>> script,
        int maxExchanges = 80,
        CancellationToken cancellationToken = default)
    {
        List<DrivenExchange> exchanges = [];
        InterviewState state = await interview.StartAsync(cancellationToken);

        for (int i = 0; i < maxExchanges; i++)
        {
            bool written = state is { Pass: InterviewPass.AgentFormatter, ReadyForReview: true };

            if (written)
            {
                bool more = await interview.AcceptAsync(cancellationToken);
                if (!more)
                    break;

                state = interview.Current!;
                continue;
            }

            Queue<string> queue = script.TryGetValue(state.Pass, out Queue<string>? q) ? q : new Queue<string>();
            string answer = queue.Count > 0 ? queue.Dequeue() : "That's right, go ahead.";

            exchanges.Add(new DrivenExchange(state.Pass, state.Question ?? string.Empty, answer));
            state = await interview.AnswerAsync(answer, cancellationToken);

            if (state.Error is not null)
                break;
        }

        return new DrivenInterview(state, exchanges);
    }
}
