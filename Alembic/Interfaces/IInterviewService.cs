using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Conducts the functional interview and folds its result into the Draft, one interview per circuit.
/// The state machine is C#, which pass is running and what may be written next, while the conducting is the model's: which question to ask
/// and how to turn an answer into dispositive prose. There is no Advance: a pass boundary is crossed inside <see cref="AnswerAsync"/> once the
/// state machine confirms the pass settled, a new agent and session for the model while the client's transcript stays continuous.
/// </summary>
public interface IInterviewService
{
    /// <summary>The interview in progress; <c>null</c> before one starts.</summary>
    InterviewState? Current { get; }

    /// <summary>Begins the interview at the domain map and asks the opening question.</summary>
    Task<InterviewState> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an agent already in the domain at its Target, so the client can have it corrected; reached from the walk alone, which keeps leafing free.
    /// An edit is a compose walk seeded from an existing agent, so it starts at the Target and the chain of <see cref="AnswerAsync"/> carries it through
    /// every later pass in Correcting mode: a change to one section makes the model recheck what comes after it.
    /// </summary>
    /// <param name="agentID">The agent, by the intent name that reaches it.</param>
    /// <param name="cancellationToken">Stops the model call that opens the step.</param>
    /// <returns><c>false</c> when the domain holds no such agent.</returns>
    Task<bool> ReviseAsync(
        string agentID,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an agent of today's map at its Target for correction, setting the interview aside until the correction hands it back where it stood.
    /// The correction runs in Correcting mode and invalidates nothing downstream; it ends when the agent is accepted again or its chain reaches the step the interview stood on.
    /// </summary>
    /// <param name="entry">The map entry, at or before the one in hand.</param>
    /// <param name="cancellationToken">Stops the model call that opens the step.</param>
    /// <returns><c>false</c> when nothing can be reopened: the map is still being drawn, a correction is already open or the entry has no agent.</returns>
    Task<bool> ReopenAsync(int entry, CancellationToken cancellationToken = default);

    /// <summary>Records the client's answer and asks the next question, crossing into the next pass when the running one is settled.</summary>
    /// <param name="answer">What the client said.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Steps the interview back one step of the journey, keeping everything already written; going back is the client's claim to make, going on the state machine's.
    /// The step is re-entered with a fresh agent and session reading what is written as settled fact, so the client changes what they came to change
    /// instead of dictating it again; stepping out of an entry takes that agent back out of the domain, so no second copy of it is committed.
    /// </summary>
    /// <returns><c>true</c> when it moved; <c>false</c> when there is nothing behind it.</returns>
    Task<bool> BackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds the agent just finished into the Draft, creating one if there is none, then moves the interview to the next entry of the map.
    /// Everything committed is marked <see cref="Provenance.Authored"/>, since it exists in no uploaded file and the migration report has to say so.
    /// </summary>
    /// <returns><c>true</c> when the map still has an entry and the interview goes on with it; <c>false</c> when the last one is written.</returns>
    Task<bool> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards the interview without touching the Draft.</summary>
    void Abandon();
}