using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Conducts the functional interview and folds its result into the Draft.
/// </summary>
/// <remarks>The state machine is C# while the conducting is the model's; one interview lives per circuit.</remarks>
public interface IInterviewService
{
    /// <summary>
    /// The interview in progress, or <c>null</c> before one starts.
    /// </summary>
    InterviewState? Current { get; }

    /// <summary>
    /// Begins the interview at the domain map and asks the opening question.
    /// </summary>
    Task<InterviewState> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an agent already in the domain, at its Target, so the client can have it corrected.
    /// </summary>
    /// <param name="agentID">The agent, by the intent name that reaches it.</param>
    /// <param name="cancellationToken">Stops the opening question when the client leaves the page.</param>
    /// <returns><c>false</c> when the domain holds no such agent.</returns>
    /// <remarks>An edit always opens at the Target and chains forward through every pass, in Correcting mode.</remarks>
    Task<bool> ReviseAsync(
        string agentID,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an agent of today's map at its Target for correction, setting the interview aside until
    /// the correction hands it back where it stood.
    /// </summary>
    /// <param name="entry">The map entry, at or before the one in hand.</param>
    /// <param name="cancellationToken">Stops the opening question when the client leaves the page.</param>
    /// <returns><c>false</c> when nothing can be reopened: the map is still being drawn, a correction is already open or the entry has no agent.</returns>
    /// <remarks>The correction ends when the agent is accepted again or when its chain reaches the step the interview stood on.</remarks>
    Task<bool> ReopenAsync(int entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the client's answer and asks the next question.
    /// </summary>
    /// <param name="answer">What the client said.</param>
    /// <param name="cancellationToken">Stops the turn when the client leaves the page.</param>
    Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Steps the interview back one step of the journey, keeping everything already written.
    /// </summary>
    /// <returns><c>true</c> if it moved, <c>false</c> when there is nothing behind it.</returns>
    /// <remarks>Going back is a claim only the client can make while going on is settled by the state machine.</remarks>
    Task<bool> BackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds the agent just finished into the Draft and moves the interview to the next entry of the domain map.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the map still has an entry and the interview goes on with it, <c>false</c>
    /// when the last one is written and the interview is over.
    /// </returns>
    /// <remarks>Everything committed is marked <see cref="Provenance.Authored"/> so the migration report can say it exists in no uploaded file.</remarks>
    Task<bool> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the interview without touching the Draft.
    /// </summary>
    void Abandon();
}
