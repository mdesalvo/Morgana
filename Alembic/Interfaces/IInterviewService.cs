using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Conducts the functional interview and folds its result into the Draft.
/// </summary>
/// <remarks>
/// Scoped, which in Blazor Server means one interview per circuit.
/// <para>
/// The division of labour is fixed and is the reason this service exists rather than a bare chat
/// loop: the <b>state machine is C#</b> — which pass is running, which fields are set, what may be
/// written next — and the <b>conducting is the model's</b> — which question to ask, and how to turn
/// a domain expert's answer into dispositive prose. Facts about the configuration are never left to
/// a model's discretion, and phrasing is never left to a template.
/// </para>
/// </remarks>
public interface IInterviewService
{
    /// <summary>
    /// The interview in progress, or <c>null</c> before one starts.
    /// </summary>
    InterviewState? Current { get; }

    /// <summary>
    /// Begins a functional pass and asks the opening question.
    /// </summary>
    Task<InterviewState> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the client's answer and asks the next question.
    /// </summary>
    /// <param name="answer">What the client said.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds the interview's intent and agent into the Draft under construction, creating a Draft
    /// if there is none, and ends the interview.
    /// </summary>
    /// <remarks>
    /// Everything committed is marked <see cref="Provenance.Authored"/>: it exists in no uploaded
    /// file, and the migration report has to be able to say so.
    /// </remarks>
    void Commit();

    /// <summary>
    /// Discards the interview without touching the Draft.
    /// </summary>
    void Abandon();
}
