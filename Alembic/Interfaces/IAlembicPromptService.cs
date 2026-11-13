using Morgana.AI;

namespace Alembic.Interfaces;

/// <summary>
/// Resolves Alembic's own conducting prompts from <c>alembic.json</c> and composes them under Morgana's own layer.
/// Alembic is an agent of Morgana producing agents of Morgana, so <c>alembic.json</c> has an agent's shape and its prompts are fenced beneath
/// her Personality, Target and policy names, resolved live from <c>morgana.json</c> so that tuning the framework never leaves a stale copy here.
/// The policies' bodies and her Formatting stay out: they govern a channel turn Alembic does not have.
/// </summary>
public interface IAlembicPromptService
{
    /// <summary>Resolves one of Alembic's prompts by id, throwing <see cref="KeyNotFoundException"/> when none carries it.</summary>
    /// <param name="promptId">The prompt's id, such as <c>DomainMapper</c> or <c>CodeMocker</c>.</param>
    /// <returns>The prompt.</returns>
    Records.Prompt Resolve(string promptId);

    /// <summary>
    /// Renders one interviewer into the system prompt it conducts with: Morgana's layer, then Alembic's, fenced.
    /// Alembic's half is three rows read as one, section by section under one set of labels: what holds in every interview,
    /// the <c>Composing</c> or <c>Correcting</c> row naming this step's job and what is the interviewer's own.
    /// The job is a row rather than clauses inside shared prose, which had the model read both jobs in every pass.
    /// </summary>
    /// <param name="interviewerId">The interviewer of this step: <c>DomainMapper</c>, <c>AgentTarget</c>, <c>AgentToolkit</c>, <c>AgentInstructions</c> or <c>AgentFormatting</c>.</param>
    /// <param name="correcting">Whether the step reopens an agent that already exists rather than writing one.</param>
    /// <returns>The system prompt.</returns>
    Task<string> ComposeAsync(string interviewerId, bool correcting = false);

    /// <summary>
    /// Renders the framework as a block of facts about what it already does around anything Alembic writes.
    /// Every pass that judges or writes an agent's prose reads it, since a pass with no account of the framework's rules decides by resemblance.
    /// It is authored in the <c>MorganaPrimer</c> prompt in the descriptive voice, the rules in <c>morgana.json</c> being orders to an agent taking a turn;
    /// the policy names stay <c>morgana.json</c>'s and a policy without a line throws, as does a line naming no policy.
    /// </summary>
    /// <returns>The primer, self-delimited and ready to stand above a pass's own prose.</returns>
    Task<string> ComposeFrameworkPrimerAsync();
}