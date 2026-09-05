using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Conducts the functional interview and folds its result into the Draft.
/// </summary>
/// <remarks>
/// Scoped, which in Blazor Server means one interview per circuit.
/// <para>
/// The division of labour is fixed and is the reason this service exists rather than a bare chat
/// loop: the <b>state machine is C#</b> — which pass is running, which fields are set, what may be
/// written next — and the <b>conducting is the model's</b> — which question to ask and how to turn
/// a domain expert's answer into dispositive prose. Facts about the configuration are never left to
/// a model's discretion and phrasing is never left to a template.
/// </para>
/// </remarks>
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>false</c> when the domain holds no such agent.</returns>
    // Reached from the walk and from nowhere else. That is what keeps leafing through a domain free:
    // the walk moves a marker and the model, the session and the agent leaving the configuration all
    // wait until the client actually asks to edit it.
    //
    // Always the Target, never the section the client meant to fix: an edit is a compose walk seeded
    // from an existing agent rather than a blank one, so it starts where a compose starts and
    // AnswerAsync's chain carries it through every pass after — the same chain a fresh agent walks,
    // running in Correcting mode throughout. That is what makes a change to one section force the
    // model to recheck what comes after it instead of trusting nothing downstream moved.
    //
    // Everything an edit needs already existed. A step is re-entered with a fresh agent and a fresh
    // session reading what is written as settled fact, which is what BackAsync does; an entry in hand
    // is out of the domain so it cannot be committed twice, which is what stepping between entries
    // does; and removing a tool or a parameter is DropTool and DropToolParameter, which the toolkit
    // pass has declared all along. What was missing was a way in from outside the interview.
    Task<bool> ReviseAsync(
        string agentID,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the client's answer and asks the next question.
    /// </summary>
    /// <param name="answer">What the client said.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default);

    // There is deliberately no Advance: a pass boundary is not the client's to cross. It is a new
    // agent and a new session, only the configuration goes over it and it happens inside
    // AnswerAsync the moment the state machine confirms the running pass settled. The client's
    // transcript is continuous — they are having one interview and the restart is the model's alone.

    /// <summary>
    /// Steps the interview back one step of the journey, keeping everything already written.
    /// </summary>
    /// <returns><c>true</c> if it moved, <c>false</c> when there is nothing behind it.</returns>
    // Backwards is the client's where forwards is not: going on is a claim that a step is settled,
    // which the state machine decides and going back is a claim that something needs changing,
    // which only they can make.
    //
    // The memory is the configuration, not the conversation. A step is re-entered the way it was
    // entered the first time — fresh agent, fresh session, reading what is written as settled fact
    // — so the client changes what they came to change instead of dictating it again. Stepping out
    // of an entry into the one before takes that agent back out of the domain, so no second copy of
    // it can be committed.
    Task<bool> BackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds the agent just finished into the Draft, creating a Draft if there is none and moves
    /// the interview to the next entry of the domain map.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the map still has an entry and the interview goes on with it, <c>false</c>
    /// when the last one is written and the interview is over.
    /// </returns>
    /// <remarks>
    /// Everything committed is marked <see cref="Provenance.Authored"/>: it exists in no uploaded
    /// file and the migration report has to be able to say so. The fallback intent is put in the
    /// same Draft if it is not there — it belongs to every domain and to no interview.
    /// </remarks>
    Task<bool> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the interview without touching the Draft.
    /// </summary>
    void Abandon();
}
