using Morgana.AI;

namespace Distiller.Interfaces;

/// <summary>
/// Resolves Alembic's own conducting prompts from <c>alembic.json</c>.
/// </summary>
/// <remarks>
/// Alembic is an agent of Morgana that produces agents of Morgana, so it is composed the way one
/// is: layered, fenced and subordinate to her. <c>alembic.json</c> is a
/// <see cref="Records.PromptCollection"/> with the same four sections an agent has, embedded the
/// same way <c>morgana.json</c> is embedded in Morgana.AI. Whoever tunes Alembic does the job
/// Alembic teaches.
/// <para>
/// The topmost layer is <b>Morgana in her own words, resolved live</b> from <c>morgana.json</c>
/// rather than copied: her <c>Personality</c>, because her identity is Alembic's identity; her
/// <c>Target</c>, because it is the only place that says what an agent <em>of</em> Morgana is and
/// the lower of the two layers it describes is exactly what Alembic writes; and her
/// <c>GlobalPolicies</c> by name, as the list of subjects already settled above every agent. A copy
/// of any of it would drift the day the framework is tuned.
/// </para>
/// <para>
/// Left out: the policies' bodies and her <c>Formatting</c>. Those govern how a <em>channel turn</em>
/// is formed — quick replies, rich cards, turn continuation, markdown for a rendered surface — and
/// Alembic has no channel, no Guard, no Classifier and no turn in that sense. Handing it rules about
/// things that do not exist in its world is the most direct way to manufacture the non-local
/// contradictions this whole project exists to avoid. What carries over is who she is and what she
/// binds; what does not is the mechanics of a conversation Alembic is not having.
/// </para>
/// </remarks>
public interface IAlembicPromptService
{
    /// <summary>
    /// Resolves one of Alembic's prompts by ID.
    /// </summary>
    /// <param name="promptId">e.g. <c>DomainMapper</c>, <c>CodeMocker</c>.</param>
    /// <returns>The prompt.</returns>
    /// <exception cref="KeyNotFoundException">No prompt carries that ID.</exception>
    Records.Prompt Resolve(string promptId);

    /// <summary>
    /// Renders one interviewer into the system prompt it conducts with.
    /// </summary>
    /// <remarks>
    /// Two layers, fenced — the same shape Alembic teaches, applied to Alembic: Morgana, then
    /// Alembic. Alembic's own half is stored in three rows and read as one: the <c>Alembic</c> prompt
    /// says what holds in every interview, <c>Composing</c> or <c>Correcting</c> says which of the two
    /// jobs this step is and an interviewer says only what is its own. They are merged section by
    /// section under one set of labels, so what the model reads is still the four sections an agent
    /// prompt always is, with no seam in it.
    /// <para>
    /// The mode is a row rather than a branch inside the prose and rather than a second copy of the
    /// whole file. Written as clauses — "composing, ask this; correcting, ask that" — the two jobs sat
    /// in every pass and the model read both every time, which is how it opened a written agent as a
    /// blank one twice over. Written as two files they would have been nine tenths identical, tool
    /// declarations included, which is the duplication this prompt was already once rebuilt to remove.
    /// A row costs neither: what differs is stored once and a pass is handed only the half that is
    /// true of it.
    /// </para>
    /// </remarks>
    /// <param name="interviewerId">Which interviewer conducts this step: <c>DomainMapper</c>, <c>AgentTarget</c>, <c>AgentToolkit</c>, <c>AgentInstructions</c>, <c>AgentFormatting</c>.</param>
    /// <param name="correcting">Whether this step is reopening an agent that already exists, rather than writing one.</param>
    /// <returns>The system prompt.</returns>
    Task<string> ComposeAsync(string interviewerId, bool correcting = false);
}
