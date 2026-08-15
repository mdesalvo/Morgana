using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="ISolutionEmitService"/>: string templates, and nothing else — same discipline
/// as <see cref="CodeEmitService"/>, applied one level up, to the project that houses its output.
/// </summary>
/// <remarks>
/// Without this, the archive is loose sources: a client unzips it and still has to create a class
/// library, add the Morgana.AI reference, and embed agents.json by hand before anything builds.
/// With it, the same unzip is already <c>dotnet build</c> — assuming a NuGet feed that resolves
/// Morgana.AI, which is the one thing here Alembic cannot see or guarantee.
/// </remarks>
public class SolutionEmitService : ISolutionEmitService
{
    /// <summary>
    /// The .NET the emitted project targets — the same one Alembic and Morgana.AI are built on.
    /// </summary>
    private const string TargetFramework = "net10.0";

    /// <inheritdoc />
    public IReadOnlyList<EmittedFile> Emit(DomainDraft draft)
    {
        string ns = draft.Agents.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Code.Namespace))?.Code.Namespace
                    ?? CodeEmitService.DefaultNamespace;

        // One version for both: Morgana.AI and Morgana.Contracts are built and released together
        // from the same Directory.Build.props, and never drift apart from one another.
        string version = PackageVersion(typeof(Morgana.AI.Records));

        string csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

                <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>{TargetFramework}</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <RootNamespace>{ns}</RootNamespace>
                    <AssemblyName>{ns}</AssemblyName>
                </PropertyGroup>

                <ItemGroup>
                    <!-- MorganaAgentAdapter reads this back out of the plugin's own assembly at startup. -->
                    <EmbeddedResource Include="agents.json" />
                </ItemGroup>

                <ItemGroup>
                    <PackageReference Include="Morgana.AI" Version="{version}" />
                    <!--
                      Arrives transitively through Morgana.AI already, but named here explicitly:
                      QuickReply, RichCard and the rest of the wire contracts a tool body or a
                      Formatting-shaped reply may want to construct directly are types the client
                      should see and navigate to, not ones they discover only when the compiler
                      resolves them.
                    -->
                    <PackageReference Include="Morgana.Contracts" Version="{version}" />
                </ItemGroup>

            </Project>
            """;

        string slnx = $"""
            <Solution>
              <Folder Name="/Solution Items/">
                <File Path="agents.json" />
                <File Path="README.md" />
                <File Path="MIGRATION.md" />
              </Folder>
              <Project Path="{ns}.csproj" />
            </Solution>
            """;

        return
        [
            new EmittedFile($"{ns}.csproj", csproj, FileOwnership.Generated),
            new EmittedFile($"{ns}.slnx", slnx, FileOwnership.Generated)
        ];
    }

    /// <summary>
    /// The version to pin a package reference to: whichever build of it this very Alembic is
    /// running against, read off the assembly of one of its own types rather than restated by hand
    /// somewhere it would drift the day the framework ships a new one.
    /// </summary>
    private static string PackageVersion(Type typeFromThatPackage)
    {
        Version? version = typeFromThatPackage.Assembly.GetName().Version;
        return version is null ? "*" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
