using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PromptHarness.Infrastructure;

/// <summary>
/// Whether the archive <see cref="ArchiveCompiler"/> extracted built, and the full <c>dotnet build</c>
/// transcript either way.
/// </summary>
public sealed record ArchiveCompileResult(bool Succeeded, int ExitCode, string Output);

/// <summary>
/// Unzips an archive <c>IAssetPackageService</c> produced into a throwaway directory and builds it
/// for real — the only way to know the generated <c>.g.cs</c> and the model-authored mock actually
/// compile together, the same question a client's own <c>dotnet build</c> answers on the first run.
/// </summary>
/// <remarks>
/// The emitted <c>.csproj</c> references <c>Morgana.AI</c>/<c>Morgana.Contracts</c> by
/// <c>PackageReference</c>, pinned to whichever build Alembic itself is running against — exactly
/// right for a client, whose machine resolves it from a real feed, and exactly wrong for this
/// harness, run against a checkout of a version that has not been published anywhere yet. So this
/// rewrites those two lines to <c>ProjectReference</c>s against the local source before building,
/// the same substitution done by hand while chasing the CS0579 mock-attribute bug this suite now
/// guards against — verifying the same thing <c>Examples/agents.json</c> was once verified against,
/// a real feed, is a separate concern belonging to a release check, not this suite.
/// </remarks>
public static class ArchiveCompiler
{
    /// <summary>
    /// This repository's root, derived from this very file's own compile-time path rather than a
    /// hardcoded machine path — the harness lives at <c>{repo}/Alembic/PromptHarness/Infrastructure/</c>
    /// on every checkout, wherever that checkout happens to sit.
    /// </summary>
    private static readonly string RepoRoot = ComputeRepoRoot();

    private static string ComputeRepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)!)!)!)!;

    private static readonly string MorganaAIProject =
        Path.Combine(RepoRoot, "Morgana", "Morgana.AI", "Morgana.AI.csproj");

    private static readonly string MorganaContractsProject =
        Path.Combine(RepoRoot, "Morgana", "Morgana.Contracts", "Morgana.Contracts.csproj");

    private static readonly Regex MorganaAIPackageReference =
        new("""<PackageReference Include="Morgana\.AI" Version="[^"]*" />""", RegexOptions.Compiled);

    private static readonly Regex MorganaContractsPackageReference =
        new("""<PackageReference Include="Morgana\.Contracts" Version="[^"]*" />""", RegexOptions.Compiled);

    /// <summary>
    /// Extracts <paramref name="archive"/> into a fresh temporary directory, retargets its
    /// framework references to the local source, and runs <c>dotnet build</c> against it.
    /// </summary>
    public static async Task<ArchiveCompileResult> ExtractAndBuildAsync(byte[] archive, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AlembicHarness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        using (MemoryStream buffer = new MemoryStream(archive))
        using (ZipArchive zip = new ZipArchive(buffer, ZipArchiveMode.Read))
            zip.ExtractToDirectory(dir);

        string csprojPath = Directory.GetFiles(dir, "*.csproj").FirstOrDefault()
            ?? throw new InvalidOperationException($"No .csproj found in the extracted archive at {dir}.");

        string csproj = await File.ReadAllTextAsync(csprojPath, cancellationToken);
        csproj = MorganaAIPackageReference.Replace(csproj, $"""<ProjectReference Include="{MorganaAIProject}" />""");
        csproj = MorganaContractsPackageReference.Replace(csproj, $"""<ProjectReference Include="{MorganaContractsProject}" />""");
        await File.WriteAllTextAsync(csprojPath, csproj, cancellationToken);

        // UseSharedCompilation=false: this build's ProjectReferences point at live source under
        // active development, not a restored package — the same machine is liable to have another
        // build (the IDE, a concurrent test run) touching the very same VBCSCompiler pipe at the
        // same moment.
        ProcessStartInfo psi = new("dotnet", "build -v quiet -p:UseSharedCompilation=false")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ArchiveCompileResult(process.ExitCode == 0, process.ExitCode, stdout + stderr);
    }
}