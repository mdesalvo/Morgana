using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Fixtures;

/// <summary>
/// A correction that reaches past the section it was made in: a capability added to an agent's
/// <c>Target</c> that no tool of that agent backs.
/// </summary>
/// <remarks>
/// The hazard editing has and composing does not. Writing an agent, the toolkit is settled after the
/// Target and against it, so a capability without a tool cannot outlive the step that would have
/// given it one. Correcting, the client changes what the agent promises in its Target — but an edit
/// re-enters at the Target and walks every section after it exactly as a fresh compose does, so this
/// is not left for a later pass to notice from the outside: the walk itself carries the change into
/// the Toolkit, where the same tool this fixture would use to compose one from scratch is available
/// to close the gap.
/// <para>
/// <c>Inventory</c> is the subject, and it is the hardest of the four rather than the easiest. It
/// carries eight tools — catalogue, stock, orders, cancellation, history — so the toolkit quoted to
/// the pass looks comprehensive, and none of the eight issues anything. A gap surrounded by that much
/// coverage is the one a pass is most likely to read past, which is exactly the case worth holding it
/// to. An agent with no tools at all would prove nothing either way: that is the legal MCP-only
/// shape.
/// </para>
/// </remarks>
public sealed class AddedCapabilityFixture : IAsyncLifetime
{
    private const string DomainFile = "examples-agents.json";

    /// <summary>Which agent this fixture corrects, by the intent name that reaches it.</summary>
    public const string Edited = "Inventory";

    /// <summary>
    /// The capability the client adds, at the Target step. Deliberately an ACTION on a system rather
    /// than a turn of phrase — issuing something, which no tool of this agent does — so the toolkit
    /// it leaves short is a fact about the domain and not a matter of taste.
    /// </summary>
    public const string AddedCapability =
        "One more thing it should handle: once an order is closed successfully and came to at least 75 "
        + "dollars, it issues the customer a 10 dollar coupon to use on their next one.";

    /// <summary>
    /// What the client says once the walk reaches the Toolkit, supplying the tool the Target now
    /// promises.
    /// </summary>
    public const string AddedToolAnswer =
        "Yes, add one for it: when an order closes at 75 dollars or more, it should create a 10 "
        + "dollar coupon for the customer to use on their next order.";

    private readonly AlembicHostFixture host;
    private IServiceScope scope = null!;

    /// <summary>Every turn of the correction, oldest first.</summary>
    public IReadOnlyList<DrivenExchange> Exchanges { get; private set; } = [];

    /// <summary>Everything Alembic said across the correction, for the propositions that allow it anywhere.</summary>
    public string EverythingSaid { get; private set; } = string.Empty;

    /// <summary>The agent's toolkit as it arrived, and as it stands after the correction.</summary>
    /// <remarks>
    /// Compared to each other rather than to a list written here, so the assertion stays true if
    /// somebody legitimately edits the Examples domain's other tools.
    /// </remarks>
    public IReadOnlyList<string> ToolsBefore { get; private set; } = [];

    /// <inheritdoc cref="ToolsBefore" />
    public IReadOnlyList<string> ToolsAfter { get; private set; } = [];

    public IServiceProvider Services => scope.ServiceProvider;

    public AddedCapabilityFixture(AlembicHostFixture host) => this.host = host;

    /// <summary>Renders the correction as a scrollback, for a failing assertion's message.</summary>
    public string Transcript => string.Join('\n', Exchanges.Select(e => $"[{e.Pass}] Q: {e.Question}\nA: {e.Answer}"));

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        scope = host.NewScope();

        IDraftImportService import = scope.ServiceProvider.GetRequiredService<IDraftImportService>();
        IDraftStateService draftState = scope.ServiceProvider.GetRequiredService<IDraftStateService>();
        IInterviewService interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();

        await using FileStream file = File.OpenRead(DomainFile);
        DraftImportResult imported = await import.ImportAsync(file, DomainFile);

        DomainDraft draft = imported.Draft
                            ?? throw new InvalidOperationException($"{DomainFile} did not import: {imported.Error}");

        draftState.Set(draft);

        ToolsBefore = Toolkit(draft);

        if (!await interview.ReviseAsync(Edited))
            throw new InvalidOperationException($"The domain holds no agent '{Edited}' to correct.");

        // Only the Target and the Toolkit get anything to say: everything else the edit passes
        // through (Personality, Instructions, Formatting) is left for the driver's own fallback
        // agreement, exactly as an unaffected section is expected to settle in one quick confirm.
        Dictionary<InterviewStep, Queue<string>> script = new()
        {
            [InterviewStep.AgentTarget] = new Queue<string>([AddedCapability]),
            [InterviewStep.AgentToolkit] = new Queue<string>([AddedToolAnswer])
        };

        DrivenInterview driven = await InterviewDriver.RunEditAsync(interview, script);

        Exchanges = driven.Exchanges;
        EverythingSaid = string.Join("\n\n", driven.Exchanges
            .Select(e => e.Question)
            .Where(q => q.Length > 0));

        ToolsAfter = Toolkit(draftState.Current!);
    }

    /// <summary>What the edited agent can reach, by name, in the order it declares them.</summary>
    private static IReadOnlyList<string> Toolkit(DomainDraft draft) =>
        [.. draft.Agents
            .Single(a => string.Equals(a.ID, Edited, StringComparison.OrdinalIgnoreCase))
            .Tools.Select(tool => tool.Name ?? "(unnamed)")];

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        scope.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Groups the tests that read the one added-capability correction, so it is driven once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AddedCapabilityCollection : ICollectionFixture<AddedCapabilityFixture>
{
    /// <summary>The name every member class's <c>[Collection]</c> attribute refers back to.</summary>
    public const string Name = "Examples domain, capability added";
}
