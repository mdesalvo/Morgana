using System.IO.Compression;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IAssetPackageService"/>: assembles the archive in memory.
/// A whole domain is tens of kilobytes and Alembic has no filesystem it may write to, running wherever the client deployed it.
/// </summary>
public class AssetPackageService : IAssetPackageService
{
    /// <summary>
    /// The one entry of the archive that says Alembic built it, which Import trusts to tell its own archive from a zipped configuration.
    /// Anyone can zip an <c>agents.json</c>, while no other zip has a reason to carry this entry; what matters is that it exists, never what it says.
    /// </summary>
    public const string MarkerFileName = "alembic-emit.marker";

    /// <summary>Writes the Draft back out as <c>agents.json</c>.</summary>
    private readonly IDraftExportService draftExportService;

    /// <summary>Writes the Draft's own save file, so a re-upload of the archive resumes the interview.</summary>
    private readonly IDraftSerializationService draftSerializationService;

    /// <summary>Writes the project and solution the sources build in.</summary>
    private readonly ISolutionEmitService solutionEmitService;

    /// <summary>Writes the generated half of each agent and tool class.</summary>
    private readonly ICodeEmitService codeEmitService;

    /// <summary>Writes the client's half of each tool class, the one step that calls a model.</summary>
    private readonly IToolMockService toolMockService;

    /// <summary>Builds <c>MIGRATION.md</c> from the Draft's diff against its baseline.</summary>
    private readonly IMigrationReportService migrationReportService;

    /// <summary>Records a mock that could not be written, which the archive ships around.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the packager to the emitters and the report it assembles.</summary>
    /// <param name="draftExportService">Writes the Draft back out as <c>agents.json</c>.</param>
    /// <param name="draftSerializationService">Writes the Draft's own save file, <c>alembic-draft.json</c>.</param>
    /// <param name="solutionEmitService">Writes the project and solution files the generated sources live in.</param>
    /// <param name="codeEmitService">Writes the deterministic <c>.g.cs</c> half of each agent and tool class.</param>
    /// <param name="toolMockService">Writes the authored mock half of each tool class — the one call that can fail.</param>
    /// <param name="migrationReportService">Builds <c>MIGRATION.md</c> from the Draft's diff against its baseline.</param>
    /// <param name="logger">Used to record per-agent mock failures that the archive still ships around.</param>
    public AssetPackageService(
        IDraftExportService draftExportService,
        IDraftSerializationService draftSerializationService,
        ISolutionEmitService solutionEmitService,
        ICodeEmitService codeEmitService,
        IToolMockService toolMockService,
        IMigrationReportService migrationReportService,
        ILogger logger)
    {
        this.draftExportService = draftExportService;
        this.draftSerializationService = draftSerializationService;
        this.solutionEmitService = solutionEmitService;
        this.codeEmitService = codeEmitService;
        this.toolMockService = toolMockService;
        this.migrationReportService = migrationReportService;
        this.logger = logger;
    }

    /// <summary>
    /// Packages the whole domain in the order a client would want to trust it: the marker and the configuration, the project, then each agent's C#.
    /// A mock that fails costs only its own body, so the client still receives the configuration, the signatures and the report.
    /// </summary>
    public async Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The archive is built in memory, handed back as bytes for the browser to download.
        using MemoryStream buffer = new MemoryStream();

        // The archive lives entirely in this using block: ZipArchive buffers entries and writes the
        // central directory only on Dispose, so nothing after this point may return early — a buffer
        // read before that point is bytes no unzip program will recognise as a zip.
        await using (ZipArchive archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The marker first, so even an archive cut short by a failure is recognised as Alembic's.
            await WriteAsync(archive, MarkerFileName,
                "This file marks an archive Alembic built. It carries no information of its own and "
                + "is not part of the domain — safe to delete once you have unpacked the rest.",
                progress, cancellationToken);

            // The configuration, the save file that resumes the interview, the report and the convention, none of which needs a model.
            await WriteAsync(archive, "agents.json", Encoding.UTF8.GetString(draftExportService.Export(draft)), progress, cancellationToken);
            await WriteAsync(archive, "alembic-draft.json", Encoding.UTF8.GetString(draftSerializationService.Serialize(draft)), progress, cancellationToken);
            await WriteAsync(archive, "MIGRATION.md", migrationReportService.Build(draft).Markdown, progress, cancellationToken);
            await WriteAsync(archive, "README.md", Readme, progress, cancellationToken);

            // The project and solution the generated sources live in, so the unzipped folder is already a dotnet build.
            foreach (EmittedFile file in solutionEmitService.Emit(draft))
                await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

            // Each named agent: an agent the map has not named yet has no class to emit.
            foreach (AgentDraft agent in draft.Agents.Where(agentDraft => !string.IsNullOrWhiteSpace(agentDraft.ID)))
            {
                // The generated half: the agent's class and, where it has tools, the declared half of its tool class.
                foreach (EmittedFile file in codeEmitService.Emit(agent, agent.ID!))
                    await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

                // An agent with no native tool has no tool class to mock.
                if (agent.Tools.Count == 0)
                    continue;

                // The one call in the archive that can fail on something outside Alembic. It is
                // caught per agent rather than allowed to abort the download: a client holding the
                // configuration, the signatures and the report, minus one mock body, has everything
                // that matters. Losing all of it to one provider timeout would not.
                string toolClass = agent.Code.ToolClassName ?? $"{char.ToUpperInvariant(agent.ID![0])}{agent.ID[1..]}Tool";

                try
                {
                    // The client's half, written beside the generated one under the class both halves declare.
                    string mock = await toolMockService.AuthorAsync(agent, agent.ID!, cancellationToken);
                    await WriteAsync(archive, $"Tools/{toolClass}.cs", mock, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not author the mock for {AgentId}", agent.ID);

                    // A sibling file rather than a silently missing one: the client sees exactly
                    // which class has no body yet and what to do about it, in the same folder they
                    // would have looked in for the mock itself.
                    await WriteAsync(archive, $"Tools/{toolClass}.cs.FAILED.txt",
                        $"Alembic could not write this mock: {ex.Message}\n\n"
                        + $"Everything else in the archive is complete. Implement the partial methods declared in {toolClass}.g.cs by hand,\n"
                        + "or morganize it again.",
                        progress, cancellationToken);
                }
            }
        }

        // Read only now that the archive is closed and its central directory written.
        return buffer.ToArray();
    }

    /// <summary>Writes one entry and announces it to the caller's progress line.</summary>
    /// <param name="archive">The archive under construction.</param>
    /// <param name="path">The entry's path inside the archive, forward-slash separated.</param>
    /// <param name="content">The entry's whole text content.</param>
    /// <param name="progress">Told the entry's path once it is written, for the caller's own status line.</param>
    /// <param name="cancellationToken">Cancels the write mid-entry.</param>
    private static async Task WriteAsync(
        ZipArchive archive, string path, string content, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // Compressed as tightly as the format allows, since the download is what the client waits on.
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);

        // UTF-8 without a byte-order mark, which a compiler or a JSON reader would otherwise meet as a stray character.
        await using (Stream stream = await entry.OpenAsync(cancellationToken))
        await using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(content.AsMemory(), cancellationToken);

        // The page names each file as it lands, the mocks taking seconds each.
        progress?.Report(path);
    }

    /// <summary>The convention, carried inside the archive because there is nowhere else to carry it.</summary>
    private const string Readme = """
        # What is in here

        A Morgana domain: its configuration, the C# that backs it and a report of what changed.

        | Path | Yours or Alembic's |
        |---|---|
        | `*.csproj` / `*.slnx` | Alembic's. A ready project referencing the `Morgana.AI` package, named after your namespace |
        | `agents.json` | the configuration Morgana loads — intents and agent prose |
        | `Agents/*.g.cs` | Alembic's. Regenerated in full every time |
        | `Tools/*.g.cs` | Alembic's. Attributes, constructor and one `partial` signature per tool |
        | `Tools/*.cs` | **yours.** Written once as a working mock, never written again |
        | `MIGRATION.md` | what this differs from, if anything was uploaded |
        | `alembic-draft.json` | the interview's save file — upload it to carry on |
        | `alembic-emit.marker` | Alembic's own — lets a re-upload of this archive be recognised as one, nothing else |

        ## The two halves

        A tool class is split so regeneration is not destructive. Alembic owns the `.g.cs`: the
        attributes, the constructor and a `partial` declaration for every tool in the
        configuration. You own the `.cs`: the bodies.

        The split is not enforced anywhere and does not need to be. A declaration without an
        implementation does not compile and an implementation whose signature stops matching its
        declaration does not compile either — loudly, before a single conversation happens.

        Every parameter is a `string`, because the configuration carries no types: the schema the model
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
        """;
}