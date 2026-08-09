using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Writes the starter PromptHarness scenarios for a domain.
/// </summary>
/// <remarks>
/// <para>
/// A domain agent <em>is</em> its prose, so the only way to know a prompt revision did not break
/// anything is to make the model do the thing and look. That is what the harness is, and a client
/// who leaves Alembic without scenarios has a domain nobody can revise safely — the prose will be
/// edited, because prose always is, and nothing will be watching.
/// </para>
/// <para>
/// Alembic writes the starting set and no more. It knows what the agent was <em>designed</em> to do,
/// which is exactly the material a first scenario is made of; it does not know what will actually go
/// wrong in production, which is what every scenario after the first is made of. These are a floor,
/// never a suite.
/// </para>
/// <para>
/// LLM-authored rather than templated, and deliberately so: a scenario's value is in its
/// <c>say:</c> lines and its judge propositions, both of which are domain prose. A template would
/// fill placeholders and produce something that runs, passes, and tests nothing.
/// </para>
/// </remarks>
public interface IScenarioAuthorService
{
    /// <summary>
    /// Writes the scenarios for one agent.
    /// </summary>
    /// <param name="agent">The agent to exercise.</param>
    /// <param name="intentName">The intent that routes to it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One file per scenario, under <c>Scenarios/</c>. Empty when the model produced nothing usable
    /// — a missing scenario costs the client an afternoon, and a malformed one costs them trust in
    /// the whole suite.
    /// </returns>
    Task<IReadOnlyList<EmittedFile>> AuthorAsync(
        AgentDraft agent,
        string intentName,
        CancellationToken cancellationToken = default);
}
