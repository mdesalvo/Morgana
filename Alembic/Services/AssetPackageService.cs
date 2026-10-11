using System.Globalization;
using System.IO.Compression;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// Assembles the one archive that the client downloads: configuration, save file, project, generated C# and mocks.
/// </summary>
/// <remarks>The archive is built in memory since Alembic assumes no filesystem it may write to.</remarks>
public class AssetPackageService : IAssetPackageService
{
    /// <summary>The entry that tells Import an archive was built by Alembic, since a zip with an <c>agents.json</c> proves nothing.</summary>
    public const string MarkerFileName = "alembic-emit.marker";

    /// <summary>The entry that holds the configuration Morgana loads.</summary>
    public const string ConfigurationFileName = "agents.json";

    /// <summary>The entry that holds the interview's save file.</summary>
    public const string DraftFileName = "alembic-draft.json";

    /// <summary>The entry that reports how this archive differs from the one before it.</summary>
    public const string MigrationFileName = "MIGRATION.md";

    /// <summary>The entry that carries the two-halves convention to the client.</summary>
    public const string ReadmeFileName = "README.md";

    /// <summary>
    /// Where an agent's mock lands in the archive: the client's half of its tool class.
    /// </summary>
    public static string MockPathOf(AgentDraft agent) =>
        $"Tools/{agent.Code.ToolClassName ?? $"{char.ToUpperInvariant(agent.ID![0])}{agent.ID[1..]}Tool"}.cs";

    /// <summary>
    /// Where the note replacing a mock lands when its authoring failed, beside the mock itself.
    /// </summary>
    public static string FailedMockPathOf(string mockPath) => mockPath + ".FAILED.txt";

    /// <summary>Writes the Draft as the <c>agents.json</c> entry.</summary>
    private readonly IDraftExportService draftExportService;

    /// <summary>Writes the Draft as the save file entry and copies it for the baseline.</summary>
    private readonly IDraftSerializationService draftSerializationService;

    /// <summary>Writes the project and solution entries.</summary>
    private readonly ISolutionEmitService solutionEmitService;

    /// <summary>Writes the generated C# entries of each agent.</summary>
    private readonly ICodeEmitService codeEmitService;

    /// <summary>Authors the mock entry of each tool class.</summary>
    private readonly IToolMockService toolMockService;

    /// <summary>Writes the migration report entry.</summary>
    private readonly IMigrationReportService migrationReportService;

    /// <summary>Receives the failures of mock authoring that the archive ships around.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the package service over the emitters and reports that the archive is made of.
    /// </summary>
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

    /// <inheritdoc />
    /// <remarks>Entries are written marker and configuration first, then the project, then the C# of each agent.</remarks>
    public async Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The report reads the baseline that is the previous archive: this snapshot becomes the baseline once the archive is built.
        DateTimeOffset emittedAt = DateTimeOffset.UtcNow;
        DomainDraft snapshot = await CloneAsync(draft, cancellationToken);
        snapshot.Baseline = null;
        snapshot.Sitting = null;
        snapshot.ImportedFrom = string.Create(CultureInfo.InvariantCulture, $"the archive of {emittedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");

        // The saved copy keeps the sitting and carries the snapshot, so the next resume diffs against this archive.
        DomainDraft copy = await CloneAsync(draft, cancellationToken);
        copy.Baseline = snapshot;

        // The archive bytes accumulate here for the download.
        using MemoryStream buffer = new MemoryStream();

        // The zip is complete only when this block closes, so nothing inside it may return early.
        await using (ZipArchive archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The marker lets a re-upload of this archive be recognised as Alembic's.
            await WriteAsync(archive, MarkerFileName,
                "This file marks an archive Alembic built. It carries no information of its own and "
                + "is not part of the domain — safe to delete once you have unpacked the rest.",
                progress, cancellationToken);

            // The configuration, the save file and the migration report all come from the draft as it stands now.
            await WriteAsync(archive, ConfigurationFileName, Encoding.UTF8.GetString(draftExportService.Export(draft)), progress, cancellationToken);
            await WriteAsync(archive, DraftFileName, Encoding.UTF8.GetString(draftSerializationService.Serialize(copy)), progress, cancellationToken);
            await WriteAsync(archive, MigrationFileName, migrationReportService.Build(draft).Markdown, progress, cancellationToken);
            await WriteAsync(archive, ReadmeFileName, Readme, progress, cancellationToken);

            // The project and solution make the unzipped archive buildable as it is.
            foreach (EmittedFile file in solutionEmitService.Emit(draft))
                await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

            // An agent without an ID is a validation error and has no class to write.
            foreach (AgentDraft agent in draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)))
            {
                // The deterministic half of the agent: its class, its workflows and its tool signatures.
                foreach (EmittedFile file in codeEmitService.Emit(agent, agent.ID!))
                    await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

                // An agent without tools has no tool class and so no mock.
                if (agent.Tools.Count == 0)
                    continue;

                // The mock is the one call that can fail on something outside Alembic.
                // A failure costs the client one mock body and never the rest of the archive.
                string mockPath = MockPathOf(agent);

                try
                {
                    // The client's half of the tool class, authored as a working mock of the domain.
                    string mock = await toolMockService.AuthorAsync(agent, agent.ID!, cancellationToken);
                    await WriteAsync(archive, mockPath, mock, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The client's cancellation still aborts the build; any other failure is reported in the archive.
                    logger.LogError(ex, "Could not author the mock for {AgentId}", agent.ID);

                    // The note beside the missing mock tells the client which class has no body yet and what to do.
                    await WriteAsync(archive, FailedMockPathOf(mockPath),
                        $"Alembic could not write this mock: {ex.Message}\n\n"
                        + $"Everything else in the archive is complete. Implement the partial methods declared in {Path.GetFileNameWithoutExtension(mockPath)}.g.cs by hand,\n"
                        + "or morganize it again.",
                        progress, cancellationToken);
                }
            }
        }

        // Set only here: a build that threw above leaves the draft diffing against the archive the client last received.
        draft.Baseline = snapshot;

        // The finished zip goes to the download.
        return buffer.ToArray();
    }

    /// <summary>Copies a Draft through its own save format, so the copy is exactly what a resume would read.</summary>
    private async Task<DomainDraft> CloneAsync(DomainDraft draft, CancellationToken cancellationToken)
    {
        using MemoryStream bytes = new MemoryStream(draftSerializationService.Serialize(draft));

        // A Draft that was just serialized and cannot be read back signals a defect in the save format.
        return await draftSerializationService.DeserializeAsync(bytes, cancellationToken)
               ?? throw new InvalidOperationException("The Draft could not be copied through its own save format.");
    }

    /// <summary>Writes one text entry into the archive and tells the client's status line which one.</summary>
    /// <param name="path">The entry's path inside the archive, forward-slash separated.</param>
    /// <param name="progress">Receives the entry's path once it is written.</param>
    /// <param name="cancellationToken">Stops the write when the client leaves the page.</param>
    private static async Task WriteAsync(
        ZipArchive archive, string path, string content, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // The entry is the file the client finds under this path after unzipping.
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);

        // The text is written as UTF-8 without a byte-order mark, as the files are read back by Morgana.
        await using (Stream stream = await entry.OpenAsync(cancellationToken))
        await using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(content.AsMemory(), cancellationToken);

        // The client sees the entry as soon as it is in the archive.
        progress?.Report(path);
    }

    /// <summary>The text of the archive's README: what each entry is and which half of a tool class is the client's.</summary>
    private const string Readme = """
        # What is in here

        A Morgana domain: its configuration, the C# that backs it and a report of what changed.

        | Path | Yours or Alembic's |
        |---|---|
        | `*.csproj` / `*.slnx` | Alembic's. A ready project referencing the `Morgana.AI` package, named after your namespace |
        | `agents.json` | the configuration Morgana loads — intents and agent prose |
        | `Agents/*.g.cs` | Alembic's. Regenerated in full every time |
        | `Workflows/*.g.cs` | Alembic's. One class per workflow: its steps, its edges and the values they carry. Regenerated in full every time |
        | `Tools/*.g.cs` | Alembic's. Attributes, constructor, one attributed `partial` signature per tool and the record it returns |
        | `Tools/*.cs` | **yours.** Written once as a working mock, never written again |
        | `MIGRATION.md` | what this differs from: the archive you received before this one, if there was one |
        | `alembic-draft.json` | the interview's save file — upload it to carry on |
        | `alembic-emit.marker` | Alembic's own — lets a re-upload of this archive be recognised as one, nothing else |

        ## The two halves

        A tool class is split so regeneration is not destructive. Alembic owns the `.g.cs`: the
        attributes, the constructor and a `partial` declaration for every tool, carrying the
        description, the approval and the scope of each parameter, with the record it returns. You
        own the `.cs`: the bodies.

        The split is not enforced anywhere and does not need to be. A declaration without an
        implementation does not compile and Morgana's startup refuses a tool class that declares
        a tool incompletely — loudly, before a single conversation happens.

        Every parameter is a `string`: the schema the model reads is generated from your method, so
        the type lives in the C# and only there. Narrow one where it should be narrower, in both
        halves.

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
