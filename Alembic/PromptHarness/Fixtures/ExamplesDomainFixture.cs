using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Fixtures;

/// <summary>
/// The shipped <c>Examples</c> domain, imported and then corrected the way a client corrects one:
/// an agent that already exists, reopened at its Target and walked through every section, with only
/// its Personality actually asked to change.
/// </summary>
/// <remarks>
/// Editing starts where an interview does not — from a configuration somebody already runs — so
/// this fixture starts there too, rather than interviewing a domain into existence first. The
/// <c>Examples</c> domain is the honest subject: four agents, real prose, real toolkits, and every
/// element marked <see cref="Provenance.Imported"/> exactly as a client's upload would be.
/// <para>
/// The agent it corrects is <c>Contract</c>, and it is the second of four on purpose. An agent
/// under correction leaves the domain so it cannot be committed twice, and the whole question of
/// putting it back where it came from is invisible on a domain of one and answered by luck on the
/// last of four.
/// </para>
/// <para>
/// One correction, run once here and read by every test in the collection: each of its turns is a
/// live call on Alembic's own Performance tier, and three tests re-running it would buy three
/// copies of one answer.
/// </para>
/// </remarks>
public sealed class ExamplesDomainFixture : IAsyncLifetime
{
    /// <summary>The domain file, copied beside the test assembly by the project's own Content item.</summary>
    private const string DomainFile = "examples-agents.json";

    /// <summary>Which agent this fixture corrects, by the intent name that reaches it.</summary>
    public const string Edited = "Contract";

    /// <summary>
    /// What the client says once the section is reopened. It names a change rather than dictating
    /// prose — which is the whole arrangement — and asks for something the imported voice plainly
    /// does not already say, so a run where nothing changed is a real failure and not a coin toss.
    /// </summary>
    public const string Correction =
        "Keep it as it is but make it noticeably more concise: whoever writes in about a contract is "
        + "usually in a hurry, so fewer words and no warm-up.";

    private readonly AlembicHostFixture host;
    private IServiceScope scope = null!;

    /// <summary>The domain as it was imported, before anything was corrected.</summary>
    public DomainDraft Draft { get; private set; } = null!;

    /// <summary>The agent's own sections as they arrived, kept to diff against what came back.</summary>
    public AgentDraft Before { get; private set; } = null!;

    /// <summary>Where the agent and its intent stood in the imported domain.</summary>
    public int AgentAt { get; private set; }

    /// <inheritdoc cref="AgentAt" />
    public int IntentAt { get; private set; }

    /// <summary>The whole domain as exported before the correction, byte for byte.</summary>
    public byte[] ExportedBefore { get; private set; } = [];

    /// <summary>
    /// What Alembic said on reaching the Personality pass — the turn under judgement. Not the first
    /// thing said overall: the edit reopens at the Target, ahead of the section actually asked to
    /// change.
    /// </summary>
    public string Opening { get; private set; } = string.Empty;

    /// <summary>Every turn of the correction, for a failing assertion's own diagnostics.</summary>
    public IReadOnlyList<DrivenExchange> Exchanges { get; private set; } = [];

    /// <summary>The agent after the correction was let back into the domain.</summary>
    public AgentDraft After { get; private set; } = null!;

    /// <summary>The scope it all ran in, so a test resolves the same graph.</summary>
    public IServiceProvider Services => scope.ServiceProvider;

    public ExamplesDomainFixture(AlembicHostFixture host) => this.host = host;

    /// <summary>Renders the correction as a scrollback, for a failing assertion's message.</summary>
    public string Transcript =>
        $"Opening: {Opening}\n"
        + string.Join('\n', Exchanges.Select(e => $"[{e.Pass}] Q: {e.Question}\nA: {e.Answer}"));

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        scope = host.NewScope();

        IDraftImportService import = scope.ServiceProvider.GetRequiredService<IDraftImportService>();
        IDraftExportService export = scope.ServiceProvider.GetRequiredService<IDraftExportService>();
        IDraftStateService draftState = scope.ServiceProvider.GetRequiredService<IDraftStateService>();
        IInterviewService interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();

        await using FileStream file = File.OpenRead(DomainFile);
        DraftImportResult imported = await import.ImportAsync(file, DomainFile);

        Draft = imported.Draft
                ?? throw new InvalidOperationException($"{DomainFile} did not import: {imported.Error}");

        draftState.Set(Draft);
        ExportedBefore = export.Export(Draft);

        AgentDraft agent = Draft.Agents.Single(a =>
            string.Equals(a.ID, Edited, StringComparison.OrdinalIgnoreCase));

        AgentAt = Draft.Agents.IndexOf(agent);
        IntentAt = Draft.Intents.FindIndex(i => string.Equals(i.Name, Edited, StringComparison.OrdinalIgnoreCase));

        // A copy, not the instance: the correction rewrites the very object the domain holds, so a
        // reference kept here would show the new prose and every before/after assertion would pass
        // by comparing a thing to itself.
        Before = Copy(agent);

        if (!await interview.ReviseAsync(Edited))
            throw new InvalidOperationException($"The domain holds no agent '{Edited}' to correct.");

        // Only Personality gets anything to say. The edit still walks Target, Toolkit, Instructions
        // and Formatting on the way there and after it — each is expected to settle in one quick
        // confirm off the driver's own fallback agreement, which is exactly the behaviour this
        // fixture's own tests hold the other three sections to below.
        Dictionary<InterviewStep, Queue<string>> script = new()
        {
            [InterviewStep.AgentPersonality] = new Queue<string>([Correction])
        };

        DrivenInterview driven = await InterviewDriver.RunEditAsync(interview, script);

        Exchanges = driven.Exchanges;
        Opening = driven.Exchanges.FirstOrDefault(e => e.Pass == InterviewStep.AgentPersonality)?.Question ?? string.Empty;

        After = draftState.Current!.Agents.Single(a =>
            string.Equals(a.ID, Edited, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The sections of an agent as they stand, detached from the domain that holds it.</summary>
    private static AgentDraft Copy(AgentDraft agent) => new()
    {
        ID = agent.ID,
        Target = agent.Target,
        Personality = agent.Personality,
        Instructions = agent.Instructions,
        Formatting = agent.Formatting,
        Origin = agent.Origin
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        scope.Dispose();
        return ValueTask.CompletedTask;
    }
}
