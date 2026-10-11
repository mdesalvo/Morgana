using Morgana.AI;
using Morgana.AI.Adapters;

namespace Alembic.Interfaces;

/// <summary>
/// Supplies Alembic's own prompts from <c>alembic.json</c> and composes them beneath Morgana's voice into the system prompt of each pass.
/// </summary>
/// <remarks>Morgana's layer is read live from <c>morgana.json</c>, with the policies' bodies and her Formatting left out since they govern a channel turn.</remarks>
public interface IAlembicPromptService
{
    /// <summary>
    /// Resolves one of Alembic's prompts by ID.
    /// </summary>
    /// <param name="promptId">The prompt's <c>ID</c> in <c>alembic.json</c>, matched ignoring case.</param>
    /// <returns>The prompt's four sections as authored, unmerged with any other layer.</returns>
    /// <exception cref="KeyNotFoundException">No prompt carries that ID.</exception>
    Records.Prompt Resolve(string promptId);

    /// <summary>
    /// Resolves a text that a prompt of <c>alembic.json</c> declares among its <c>PromptInjections</c>, with its values spliced in.
    /// </summary>
    /// <param name="promptId">The prompt that declares the injection, e.g. <c>Alembic</c> or <c>DomainValidator</c>.</param>
    /// <param name="injectionName">The name of the injection in that prompt.</param>
    /// <param name="values">Placeholder name (without its <c>((</c> and <c>))</c>) to the value that it stands for.</param>
    /// <returns>The text the model reads.</returns>
    /// <exception cref="InvalidOperationException">The prompt declares no such injection: a pass handed a missing sentence would work wrong without any sign of it.</exception>
    string ResolvePromptInjection(string promptId, string injectionName, params (string Placeholder, string Value)[] values);

    /// <summary>
    /// Resolves a text that Alembic's tools answer the model with, from the <c>ToolInjections</c> of the <c>Alembic</c> prompt, with its values spliced in.
    /// </summary>
    /// <param name="injectionName">The name of the tool injection.</param>
    /// <param name="values">Placeholder name (without its <c>((</c> and <c>))</c>) to the value that it stands for.</param>
    /// <returns>The text the model reads as the tool's answer.</returns>
    /// <exception cref="InvalidOperationException">The prompt declares no such injection.</exception>
    string ResolveToolInjection(string injectionName, params (string Placeholder, string Value)[] values);

    /// <summary>
    /// Composes the system prompt a pass conducts with: Morgana's layer then Alembic's shared prose, the job of the step and the pass's own prose.
    /// </summary>
    /// <remarks>The three rows of Alembic's layer are merged section by section, so the model reads four sections with no seam.</remarks>
    /// <param name="interviewerId">The pass that conducts the step, by its prompt id.</param>
    /// <param name="correcting">Whether the step corrects an agent that already exists instead of writing a new one.</param>
    /// <returns>The system prompt of the pass.</returns>
    Task<string> ComposeAsync(string interviewerId, bool correcting = false);

    /// <summary>
    /// Describes to an author what the framework already does around anything Alembic writes.
    /// </summary>
    /// <remarks>Passes that judge or write an agent's prose stand it above their own prose, apart from Morgana's voice.</remarks>
    /// <returns>The primer, self-delimited and ready to stand above a pass's own prose.</returns>
    Task<string> ComposeFrameworkPrimerAsync();

    /// <summary>
    /// Binds the tools that a step offers to the instance that implements them.
    /// </summary>
    /// <remarks>Which tools a pass holds is its <see cref="Records.WorkflowStep"/> while what each one is stays on the class of the instance.</remarks>
    /// <param name="step">The step whose tools are bound.</param>
    /// <param name="toolInstance">The tool class instance that the step writes through.</param>
    /// <returns>An adapter holding one tool per name of the step, in the order that the step lists them.</returns>
    /// <exception cref="InvalidOperationException">A name of the step matches no tool of the instance's class.</exception>
    MorganaToolAdapter OfferTools(Records.WorkflowStep step, object toolInstance);
}
