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
    private readonly IDraftExportService draftExportService;
    private readonly IDraftSerializationService draftSerializationService;
    private readonly ICodeEmitService codeEmitService;
    private readonly IToolMockService toolMockService;
    private readonly IMigrationReportService migrationReportService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the package service.
    /// </summary>
    public AssetPackageService(
        IDraftExportService draftExportService,
        IDraftSerializationService draftSerializationService,
        ICodeEmitService codeEmitService,
        IToolMockService toolMockService,
        IMigrationReportService migrationReportService,
        ILogger logger)
    {
        this.draftExportService = draftExportService;
        this.draftSerializationService = draftSerializationService;
        this.codeEmitService = codeEmitService;
        this.toolMockService = toolMockService;
        this.migrationReportService = migrationReportService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using MemoryStream buffer = new MemoryStream();

        using (ZipArchive archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteAsync(archive, "agents.json", Encoding.UTF8.GetString(draftExportService.Export(draft)), progress, cancellationToken);
            await WriteAsync(archive, "alembic-draft.json", Encoding.UTF8.GetString(draftSerializationService.Serialize(draft)), progress, cancellationToken);
            await WriteAsync(archive, "MIGRATION.md", migrationReportService.Build(draft).Markdown, progress, cancellationToken);
            await WriteAsync(archive, "README.md", Readme, progress, cancellationToken);

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
                        + "or run the emit again.",
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
        | `agents.json` | the configuration Morgana loads — intents and agent prose |
        | `Agents/*.g.cs` | Alembic's. Regenerated in full every time |
        | `Tools/*.g.cs` | Alembic's. Attributes, constructor, and one `partial` signature per tool |
        | `Tools/*.cs` | **yours.** Written once as a working mock, never written again |
        | `MIGRATION.md` | what this differs from, if anything was uploaded |
        | `alembic-draft.json` | the interview's save file — upload it to carry on |

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

        1. Drop these into a class library referencing the `Morgana.AI` package.
        2. Embed `agents.json` as a resource (`<EmbeddedResource Include="agents.json" />`).
        3. Build it into the directory Morgana scans (`Morgana:Plugins:Directories`, default `plugins`).
        4. Start Morgana. Startup validates the pairing in both directions, so a mismatch is a
           message and not a mystery.

        The mocks return plausible data of your domain, so you can talk to your agents on the first
        run and hear whether their prose is right — which is what the interview was for. Replace
        them with your real integration when it is.
        """;
}
