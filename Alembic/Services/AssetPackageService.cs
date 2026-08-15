using System.IO.Compression;
using System.Text;
using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IAssetPackageService"/>: assembles the archive in memory.
/// </summary>
/// <remarks>
/// In memory, and that is not a size compromise — a whole domain is tens of kilobytes. It is the
/// same constraint that shapes everything else here: Alembic has no filesystem it may write to,
/// because at runtime it lives wherever the client deployed it.
/// </remarks>
public class AssetPackageService : IAssetPackageService
{
    /// <summary>
    /// The one file in the archive that says Alembic built it.
    /// </summary>
    /// <remarks>
    /// A zip carrying an <c>agents.json</c> is not evidence of that on its own: anyone can zip up a
    /// bare configuration, and Import's own next-step doors read differently for an archive than for
    /// a bare upload. This is the fact that check is allowed to trust, because it is the one thing a
    /// zip built anywhere else would have no reason to contain. Content over name, as everywhere else
    /// Alembic tells files apart: what matters is that the entry exists, not what is written in it.
    /// </remarks>
    public const string MarkerFileName = "alembic-emit.marker";

    private readonly IDraftExportService draftExportService;
    private readonly IDraftSerializationService draftSerializationService;
    private readonly ISolutionEmitService solutionEmitService;
    private readonly ICodeEmitService codeEmitService;
    private readonly IToolMockService toolMockService;
    private readonly IScenarioAuthorService scenarioAuthorService;
    private readonly IMigrationReportService migrationReportService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the package service.
    /// </summary>
    public AssetPackageService(
        IDraftExportService draftExportService,
        IDraftSerializationService draftSerializationService,
        ISolutionEmitService solutionEmitService,
        ICodeEmitService codeEmitService,
        IToolMockService toolMockService,
        IScenarioAuthorService scenarioAuthorService,
        IMigrationReportService migrationReportService,
        ILogger logger)
    {
        this.draftExportService = draftExportService;
        this.draftSerializationService = draftSerializationService;
        this.solutionEmitService = solutionEmitService;
        this.codeEmitService = codeEmitService;
        this.toolMockService = toolMockService;
        this.scenarioAuthorService = scenarioAuthorService;
        this.migrationReportService = migrationReportService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]> BuildAsync(
        DomainDraft draft,
        bool includeScenarios = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using MemoryStream buffer = new MemoryStream();

        using (ZipArchive archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteAsync(archive, MarkerFileName,
                "This file marks an archive Alembic built. It carries no information of its own and "
                + "is not part of the domain — safe to delete once you have unpacked the rest.",
                progress, cancellationToken);

            await WriteAsync(archive, "agents.json", Encoding.UTF8.GetString(draftExportService.Export(draft)), progress, cancellationToken);
            await WriteAsync(archive, "alembic-draft.json", Encoding.UTF8.GetString(draftSerializationService.Serialize(draft)), progress, cancellationToken);
            await WriteAsync(archive, "MIGRATION.md", migrationReportService.Build(draft).Markdown, progress, cancellationToken);
            await WriteAsync(archive, "README.md", Readme, progress, cancellationToken);

            // The project and solution the generated sources live in. Their absence used to mean
            // the archive was loose files the client first had to shelter inside a project of their
            // own; with them, the same unzip is already `dotnet build` or "open in the IDE".
            foreach (EmittedFile file in solutionEmitService.Emit(draft))
                await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

            foreach (AgentDraft agent in draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)))
            {
                foreach (EmittedFile file in codeEmitService.Emit(agent, agent.ID!))
                    await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

                if (agent.Tools.Count == 0)
                    continue;

                // The one call in the archive that can fail on something outside Alembic. It is
                // caught per agent rather than allowed to abort the download: a client holding the
                // configuration, the signatures and the report, minus one mock body, has everything
                // that matters. Losing all of it to one provider timeout would not.
                string toolClass = agent.Code.ToolClassName ?? $"{char.ToUpperInvariant(agent.ID![0])}{agent.ID[1..]}Tool";

                try
                {
                    string mock = await toolMockService.AuthorAsync(agent, agent.ID!, cancellationToken);
                    await WriteAsync(archive, $"Tools/{toolClass}.cs", mock, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not author the mock for {AgentId}", agent.ID);

                    await WriteAsync(archive, $"Tools/{toolClass}.cs.FAILED.txt",
                        $"Alembic could not write this mock: {ex.Message}\n\n"
                        + $"Everything else in the archive is complete. Implement the partial methods declared in {toolClass}.g.cs by hand,\n"
                        + "or morganize it again.",
                        progress, cancellationToken);
                }
            }

            // Last, and caught the same way. Scenarios are the most valuable thing in the archive on
            // the day someone edits the prose, and the least valuable on the day it is downloaded —
            // so a domain whose scenarios failed still ships, and says so.
            //
            // Never an early return from inside this block: the archive's central directory is
            // written when the ZipArchive is disposed, and a buffer read before that is a file no
            // unzip program will open.
            foreach (AgentDraft agent in includeScenarios
                         ? draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID))
                         : [])
            {
                try
                {
                    foreach (EmittedFile scenario in await scenarioAuthorService.AuthorAsync(agent, agent.ID!, cancellationToken))
                        await WriteAsync(archive, scenario.Path, scenario.Content, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not author scenarios for {AgentId}", agent.ID);

                    await WriteAsync(archive, $"Scenarios/{agent.ID}.FAILED.txt",
                        $"Alembic could not write starter scenarios for this agent: {ex.Message}\n\n"
                        + "Nothing else in the archive depends on them. Write them by hand against the harness's own\n"
                        + "Scenarios directory, or morganize it again.",
                        progress, cancellationToken);
                }
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes one entry and announces it.
    /// </summary>
    private static async Task WriteAsync(
        ZipArchive archive, string path, string content, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);

        await using (Stream stream = entry.Open())
        await using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(content.AsMemory(), cancellationToken);

        progress?.Report(path);
    }

    /// <summary>
    /// The convention, carried inside the archive because there is nowhere else to carry it.
    /// </summary>
    private const string Readme = """
        # What is in here

        A Morgana domain: its configuration, the C# that backs it, and a report of what changed.

        | Path | Yours or Alembic's |
        |---|---|
        | `*.csproj` / `*.slnx` | Alembic's. A ready project referencing the `Morgana.AI` package, named after your namespace |
        | `agents.json` | the configuration Morgana loads — intents and agent prose |
        | `Agents/*.g.cs` | Alembic's. Regenerated in full every time |
        | `Tools/*.g.cs` | Alembic's. Attributes, constructor, and one `partial` signature per tool |
        | `Tools/*.cs` | **yours.** Written once as a working mock, never written again |
        | `MIGRATION.md` | what this differs from, if anything was uploaded |
        | `Scenarios/*.yaml` | starter PromptHarness scenarios — yours from here |
        | `alembic-draft.json` | the interview's save file — upload it to carry on |
        | `alembic-emit.marker` | Alembic's own — lets a re-upload of this archive be recognised as one, nothing else |

        ## The two halves

        A tool class is split so regeneration is not destructive. Alembic owns the `.g.cs`: the
        attributes, the constructor, and a `partial` declaration for every tool in the
        configuration. You own the `.cs`: the bodies.

        The split is not enforced anywhere, and does not need to be. A declaration without an
        implementation does not compile, and a tool whose signature stops matching its declaration
        in `agents.json` fails Morgana's startup in `MorganaToolAdapter.AddTool` — loudly, before a
        single conversation happens.

        Every parameter is a `string`, because `agents.json` carries no types: the schema the model
        reads is generated from your method, so the type lives in the C# and only there. Narrow one
        where it should be narrower, in both halves.

        ## Running it

        Unzip and open the `.slnx`, or `dotnet build` from this folder — the project is already
        wired to `Morgana.AI` (pinned to the version this archive was built against) and already
        embeds `agents.json`. This assumes your machine can resolve that package from whatever NuGet
        feed you deploy Morgana from; if it can't yet, the archive travels intact to one that can.

        1. `dotnet build`.
        2. Build it into the directory Morgana scans (`Morgana:Plugins:Directories`, default `plugins`).
        3. Start Morgana. Startup validates the pairing in both directions, so a mismatch is a
           message and not a mystery.

        The mocks return plausible data of your domain, so you can talk to your agents on the first
        run and hear whether their prose is right — which is what the interview was for. Replace
        them with your real integration when it is.

        ## The scenarios

        A domain agent is its prose, and prose gets edited. `Scenarios/` holds the starting set:
        drop the files into PromptHarness's own `Scenarios` directory and add a test that names each
        one. They are a floor, never a suite — Alembic knows what your agents were designed to do,
        which is what a first scenario is made of, and knows nothing about what will actually go
        wrong, which is what every scenario after it is made of.

        They are **domain scenarios only**, and that is the whole of what they should be. The guard,
        the classifier, quick replies, turn continuation, rich cards, channel degradation, history
        summarization and the context cycle are framework behaviour: they hold for every domain and
        Morgana ships her own scenarios for them, maintained where the policies are. Yours sit beside
        hers and never re-test them.

        They assert that a tool ran, never what it returned. That is deliberate: an assertion on a
        mock's data fails the day you wire in the real system, which is the day the suite most needs
        to still work.

        Each one is a behavioural use-case Alembic carries — the flow the agent exists for, the
        boundary it refuses, the confirmation it owes, the detail it withholds — derived against your
        domain and written in your words. You will not have every one for every agent, and that is
        the point rather than a gap: an agent whose tools only look things up has no irreversible
        action to confirm, so that scenario was declined instead of invented.
        """;
}
