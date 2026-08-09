using Morgana.AI;

namespace Alembic.Interfaces;

/// <summary>
/// Resolves Alembic's own conducting prompts from <c>alembic.json</c>.
/// </summary>
/// <remarks>
/// Alembic is an agent of Morgana that produces agents of Morgana, so it is composed the way one
/// is: layered, fenced, and subordinate to her. <c>alembic.json</c> is a
/// <see cref="Records.PromptCollection"/> with the same four sections an agent has, embedded the
/// same way <c>morgana.json</c> is embedded in Morgana.AI. Whoever tunes Alembic does the job
/// Alembic teaches.
/// <para>
/// The topmost layer is <b>Morgana's own Personality, resolved live</b> from <c>morgana.json</c>
/// rather than copied — her identity is Alembic's identity, and a copy would drift the day someone
/// tunes her voice.
/// </para>
/// <para>
/// It is nevertheless not run through <c>IPromptComposerService</c>, and one part of Morgana's
/// framework layer is left out: her <c>GlobalPolicies</c>, her <c>Formatting</c> and her
/// <c>Target</c>. Those govern how a <em>channel turn</em> is formed — quick replies, rich cards,
/// turn continuation, the system tools every agent shares, markdown for a rendered surface — and
/// Alembic has no channel, no Guard, no Classifier and no turn in that sense.
/// Handing it rules about things that do not exist in its world is the most direct way to
/// manufacture the non-local contradictions this whole project exists to avoid. What carries over
/// is who she is; what does not is the mechanics of a conversation Alembic is not having.
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
    /// Renders the shared doctrine plus one pass into the system prompt Alembic sends.
    /// </summary>
    /// <remarks>
    /// Two layers, fenced — the same shape Alembic teaches, applied to Alembic. The
    /// <c>Doctrine</c> layer carries who Alembic is, how it speaks, what the four sections of a
    /// Morgana agent mean, and what keeps an authored agent inside Morgana's universe; the pass
    /// layer carries only what this particular pass does. That split is not decoration: the
    /// doctrine is identical across passes and a pass restating any of it would be a second voice
    /// claiming the same ground, which is exactly the defect the doctrine warns the model against.
    /// </remarks>
    /// <param name="passId">Which pass to render beneath the doctrine.</param>
    /// <returns>The system prompt.</returns>
    Task<string> ComposeAsync(string passId);
}
