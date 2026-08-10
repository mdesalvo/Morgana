using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Derives the starter PromptHarness scenarios for a domain.
/// </summary>
/// <remarks>
/// <para>
/// A domain agent <em>is</em> its prose, so the only way to know a prompt revision did not break
/// anything is to make the model do the thing and look. That is what the harness is, and a client
/// who leaves Alembic without scenarios has a domain nobody can revise safely — the prose will be
/// edited, because prose always is, and nothing will be watching.
/// </para>
/// <para>
/// This runs at the end of the journey, and could not run anywhere else. A scenario is written
/// against a settled agent: the flow it exists for, the boundary its Target commits it to, the tool
/// its Instructions call for first, the detail its Formatting keeps back. Every one of those is a
/// decision an earlier pass had not yet taken.
/// </para>
/// <para>
/// <b>Templates in, domain out.</b> Alembic carries its own behavioural use-cases — a boundary
/// refused, an irreversible action confirmed, a subject that is not there — each a scenario with
/// every domain word replaced by a placeholder. What the model does is derive one against this
/// domain, in this domain's language. The split matters: which behaviours are worth protecting is
/// knowledge about agents, settled once in this repository; which words say them is knowledge about
/// the client's business, and only a model that has just read the whole domain can supply it.
/// </para>
/// <para>
/// Alembic writes the starting set and no more. It knows what the agent was <em>designed</em> to do,
/// which is exactly the material a first scenario is made of; it does not know what will actually go
/// wrong in production, which is what every scenario after it is made of. These are a floor, never a
/// suite.
/// </para>
/// <para>
/// And they are the <b>domain half</b> of that suite. Morgana ships her own scenarios for the guard,
/// the classifier, quick replies, turn continuation, rich cards, channel degradation, summarization
/// and the context cycle: that behaviour holds for every domain and is maintained where the policies
/// are. Nothing here duplicates it — no template asserts a framework key, so no derivation can.
/// </para>
/// </remarks>
public interface IScenarioAuthorService
{
    /// <summary>
    /// Derives every applicable use-case for one agent.
    /// </summary>
    /// <param name="agent">The agent to exercise.</param>
    /// <param name="intentName">The intent that routes to it. With the template name, it is the id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One file per derived scenario, under <c>Scenarios/</c>. Fewer than there are templates: a
    /// use-case this domain has no instance of is declined and dropped, which is a fact about the
    /// domain rather than a failure. Empty when nothing usable came back at all.
    /// </returns>
    Task<IReadOnlyList<EmittedFile>> AuthorAsync(
        AgentDraft agent,
        string intentName,
        CancellationToken cancellationToken = default);
}
