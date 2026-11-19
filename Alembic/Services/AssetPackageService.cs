using System.IO.Compression;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.AI.Interfaces;

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

    /// <summary>The prompt in <c>alembic.json</c> that governs writing a mock, which words the refusal of the provider.</summary>
    private const string CodeMockerPromptId = "CodeMocker";

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

    /// <summary>Reads every mock against its tools' descriptions before the archive carries it.</summary>
    private readonly IGuardService guardService;

    /// <summary>Tells whether the provider refused to write a mock, a refusal that is a verdict and not a failure.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Resolves the <c>CodeMocker</c> prompt, whose sentence the client reads when the provider refused to write a mock.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Records a mock that could not be written, which the archive ships around.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the packager to the emitters and the report it assembles.</summary>
    /// <param name="draftExportService">Writes the Draft back out as <c>agents.json</c>.</param>
    /// <param name="draftSerializationService">Writes the Draft's own save file, <c>alembic-draft.json</c>.</param>
    /// <param name="solutionEmitService">Writes the project and solution files the generated sources live in.</param>
    /// <param name="codeEmitService">Writes the deterministic <c>.g.cs</c> half of each agent and tool class.</param>
    /// <param name="toolMockService">Writes the authored mock half of each tool class — the one call that can fail.</param>
    /// <param name="migrationReportService">Builds <c>MIGRATION.md</c> from the Draft's diff against its baseline.</param>
    /// <param name="guardService">Judges every mock against its tools' descriptions before it is written.</param>
    /// <param name="llmService">Tells whether the provider refused to write a mock.</param>
    /// <param name="alembicPromptService">Resolves the <c>CodeMocker</c> prompt, which words the refusal of the provider.</param>
    /// <param name="logger">Used to record per-agent mock failures that the archive still ships around.</param>
    public AssetPackageService(
        IDraftExportService draftExportService,
        IDraftSerializationService draftSerializationService,
        ISolutionEmitService solutionEmitService,
        ICodeEmitService codeEmitService,
        IToolMockService toolMockService,
        IMigrationReportService migrationReportService,
        IGuardService guardService,
        ILanguageModelService llmService,
        IAlembicPromptService alembicPromptService,
        ILogger logger)
    {
        this.draftExportService = draftExportService;
        this.draftSerializationService = draftSerializationService;
        this.solutionEmitService = solutionEmitService;
        this.codeEmitService = codeEmitService;
        this.toolMockService = toolMockService;
        this.migrationReportService = migrationReportService;
        this.guardService = guardService;
        this.llmService = llmService;
        this.alembicPromptService = alembicPromptService;
        this.logger = logger;
    }

    /// <summary>
    /// Packages the whole domain in the order a client would want to trust it: the marker and the configuration, the project, then each agent's C#.
    /// A mock that fails to be written costs only its own body while a mock the CodeGuard refuses twice builds no archive at all, throwing <see cref="MockRefusedException"/>.
    /// </summary>
    public async Task<byte[]> BuildAsync(
        DomainDraft draft,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The agents whose mock was kept out, by the CodeGuard twice or by the provider outright, reported together once every agent has been tried.
        List<string> refusedAgentIds = [];

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
                // The generated half carries every tool's description, which is the contract the CodeGuard holds the mock to.
                string? contract = null;

                // The generated half: the agent's class and, where it has tools, the declared half of its tool class.
                foreach (EmittedFile file in codeEmitService.Emit(agent, agent.ID!))
                {
                    await WriteAsync(archive, file.Path, file.Content, progress, cancellationToken);

                    // The tool class is the file the mock completes, so its text is what the mock is judged against.
                    if (file.Path.Contains("/Tools/", StringComparison.Ordinal) || file.Path.StartsWith("Tools/", StringComparison.Ordinal))
                        contract = file.Content;
                }

                // An agent with no native tool has no tool class to mock.
                if (agent.Tools.Count == 0)
                    continue;

                // Writing a mock can fail on something outside Alembic. That failure is caught per
                // agent rather than allowed to abort the download: a client holding the configuration,
                // the signatures and the report, minus one mock body, has everything that matters.
                // A guard that cannot judge is not caught here: a mock nobody could read never ships,
                // so that failure ends the whole build.
                string toolClass = agent.Code.ToolClassName ?? $"{char.ToUpperInvariant(agent.ID![0])}{agent.ID[1..]}Tool";

                // Where the client's half lands: the name every progress report about it carries.
                string mockPath = $"Tools/{toolClass}.cs";

                // The first writing of the client's half; none comes back where the model failed or refused.
                string? mock = await AuthorOrRecordFailureAsync(archive, agent, toolClass, null);

                // Either the sibling note or the refusal already stands where the mock would have been.
                if (mock is null)
                    continue;

                // The mock is read before anything of it is written, since the client compiles and runs it unread.
                progress?.Report(new ArchiveProgress(mockPath, ArchiveProgressKind.Checking));
                GuardVerdict verdict = await guardService.JudgeMockAsync(contract ?? string.Empty, agent.Target, mock, cancellationToken);

                // A first refusal costs one more writing, told why the previous one was refused.
                if (!verdict.IsCompliant)
                {
                    progress?.Report(new ArchiveProgress(mockPath, ArchiveProgressKind.Rewriting, verdict.Violation));
                    mock = await AuthorOrRecordFailureAsync(archive, agent, toolClass, verdict.Violation);

                    // The second writing failed like a first one would have: either the sibling note or the refusal already stands and there is nothing to judge.
                    if (mock is null)
                        continue;

                    // The second writing is read exactly as the first was.
                    progress?.Report(new ArchiveProgress(mockPath, ArchiveProgressKind.Checking));
                    verdict = await guardService.JudgeMockAsync(contract ?? string.Empty, agent.Target, mock, cancellationToken);
                }

                // A second refusal keeps the mock out of the archive and the agent on the list that stops the build.
                if (!verdict.IsCompliant)
                {
                    progress?.Report(new ArchiveProgress(mockPath, ArchiveProgressKind.Refused, verdict.Violation));
                    refusedAgentIds.Add(agent.ID!);
                    continue;
                }

                // A mock the guard passed is the client's half, written beside the generated one.
                await WriteAsync(archive, mockPath, mock, progress, cancellationToken);
            }

            // Every agent was tried so the client learns all the refusals at once; the archive is never closed over a mock that was kept out.
            if (refusedAgentIds.Count > 0)
                throw new MockRefusedException(refusedAgentIds);
        }

        // Read only now that the archive is closed and its central directory written.
        return buffer.ToArray();

        #region Utilities

        // Writes the client's half of one agent's tool class or the sibling note saying why it could not be written.
        async Task<string?> AuthorOrRecordFailureAsync(ZipArchive archive, AgentDraft agent, string toolClass, string? refusal)
        {
            try
            {
                // The client's half, written under the class both halves declare.
                return await toolMockService.AuthorAsync(agent, agent.ID!, refusal, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && llmService.GetLanguageModel(Records.LLMTier.Efficiency).IsContentFilterRefusal(ex))
            {
                logger.LogWarning(ex, "The provider refused to write the mock for {AgentId}", agent.ID);

                // The sentence that the CodeMocker prompt words for this refusal, since the model wrote nothing the CodeGuard could read.
                string? refusalText = alembicPromptService.Resolve(CodeMockerPromptId)
                    .GetAdditionalPropertyOrDefault<string?>(Constants.PromptProperties.ContentFilteredMessage, null);

                // A prompt that words no such sentence would hand the client a refusal that says nothing.
                if (string.IsNullOrWhiteSpace(refusalText))
                    throw new InvalidOperationException(
                        $"The {CodeMockerPromptId} prompt declares no {Constants.PromptProperties.ContentFilteredMessage} property: the refusal of the provider cannot be worded.");

                // The client reads the refusal on the page like one of the CodeGuard's.
                progress?.Report(new ArchiveProgress($"Tools/{toolClass}.cs", ArchiveProgressKind.Refused, refusalText));

                // The build ends on the refused agents once every one has been tried, exactly as for a mock the CodeGuard refused.
                refusedAgentIds.Add(agent.ID!);

                // A refusal is the model's answer and not a mock that failed to be written, so no note stands in for the mock
                // and it is never retried: another writing would meet the same filter.
                return null;
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

                // No mock came back, so the caller has nothing to judge or write.
                return null;
            }
        }

        #endregion
    }

    /// <summary>Writes one entry and announces it to the caller's progress line.</summary>
    /// <param name="archive">The archive under construction.</param>
    /// <param name="path">The entry's path inside the archive, forward-slash separated.</param>
    /// <param name="content">The entry's whole text content.</param>
    /// <param name="progress">Told the entry's path once it is written, as a Written event for the caller's own status line.</param>
    /// <param name="cancellationToken">Cancels the write mid-entry.</param>
    private static async Task WriteAsync(
        ZipArchive archive, string path, string content, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        // Compressed as tightly as the format allows, since the download is what the client waits on.
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);

        // UTF-8 without a byte-order mark, which a compiler or a JSON reader would otherwise meet as a stray character.
        await using (Stream stream = await entry.OpenAsync(cancellationToken))
        await using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(content.AsMemory(), cancellationToken);

        // The page names each file as it lands, the mocks taking seconds each.
        progress?.Report(new ArchiveProgress(path, ArchiveProgressKind.Written));
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

        Unzip and open the `.slnx` or run `dotnet build` from this folder: the project is already
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