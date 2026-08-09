using Morgana.AI;

namespace Alembic.Interfaces;

/// <summary>
/// Resolves Alembic's own conducting prompts from <c>alembic.json</c>.
/// </summary>
/// <remarks>
/// Deliberately separate from the framework's <c>IPromptResolverService</c>, and deliberately
/// <b>not</b> composed through <c>IPromptComposerService</c>. The framework layer in
/// <c>morgana.json</c> is the law of a channel turn — quick replies, turn continuation, rich cards,
/// tool grounding — and Alembic has no channel, no Guard, no Classifier and no turn in that sense.
/// Stacking those policies onto an interviewer would issue instructions about things that do not
/// exist in its world, which is the most direct way to manufacture the non-local contradictions
/// this whole project exists to avoid.
/// <para>
/// What it does share is the <em>shape</em>: <c>alembic.json</c> is a
/// <see cref="Records.PromptCollection"/> with the same four sections an agent has, embedded the
/// same way. Whoever tunes Alembic does the job Alembic teaches.
/// </para>
/// </remarks>
public interface IAlembicPromptService
{
    /// <summary>
    /// Resolves one of Alembic's prompts by ID.
    /// </summary>
    /// <param name="promptId">e.g. <c>FunctionalPass</c>.</param>
    /// <returns>The prompt.</returns>
    /// <exception cref="KeyNotFoundException">No prompt carries that ID.</exception>
    Records.Prompt Resolve(string promptId);

    /// <summary>
    /// Renders a prompt's four sections into the system prompt Alembic sends.
    /// </summary>
    /// <remarks>
    /// One layer, not two, and no fences: fences exist to mark where a subordinate layer begins,
    /// and there is nothing beneath this one.
    /// </remarks>
    /// <param name="promptId">Which prompt to render.</param>
    /// <returns>The system prompt.</returns>
    string Compose(string promptId);
}
