using Alembic.Interfaces;
using Alembic.Models;
using System.Reflection;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="ISolutionEmitService"/>: string templates for the project that houses <see cref="CodeEmitService"/>'s output.
/// With it an unzipped archive is already a <c>dotnet build</c>, given a NuGet feed that resolves Morgana.AI, the one thing Alembic cannot see.
/// </summary>
public class SolutionEmitService : ISolutionEmitService
{
    /// <summary>The .NET the emitted project targets, the one Alembic and Morgana.AI are built on.</summary>
    private const string TargetFramework = "net10.0";

    /// <summary>Emits the <c>.csproj</c> and <c>.slnx</c> named after the domain's namespace.</summary>
    /// <param name="draft">The domain the project is emitted for, of which only the agents' namespace is read.</param>
    /// <returns>
    /// The two files, both <see cref="FileOwnership.Generated"/>: a client editing them by hand loses the edit on the next Morganize, as with any <c>.g.cs</c>.
    /// </returns>
    public IReadOnlyList<EmittedFile> Emit(DomainDraft draft)
    {
        // Every agent of one domain is emitted into one namespace, so the first agent carrying one stands for all of them.
        string projectNamespace = draft.Agents.FirstOrDefault(agent => !string.IsNullOrWhiteSpace(agent.Code.Namespace))?.Code.Namespace
                                  ?? CodeEmitService.DefaultNamespace;

        // The Morgana packages are pinned to the build this Alembic runs against, so the generated code compiles against the framework it was checked with.
        string version = PackageVersion(typeof(Morgana.AI.Records));

        // A class library embedding agents.json, where Morgana's configuration service finds it, with an uninitialized property made an error.
        string csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

                <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>{TargetFramework}</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <RootNamespace>{projectNamespace}</RootNamespace>
                    <AssemblyName>{projectNamespace}</AssemblyName>
                    <WarningsAsErrors>CS8618</WarningsAsErrors>
                </PropertyGroup>

                <ItemGroup>
                    <EmbeddedResource Include="agents.json" />
                </ItemGroup>

                <ItemGroup>
                    <PackageReference Include="Morgana.AI" Version="{version}" />
                    <PackageReference Include="Morgana.Contracts" Version="{version}" />
                </ItemGroup>

            </Project>
            """;

        // The solution shows the configuration and both reports beside the project, the files a client reads before building.
        string slnx = $"""
            <Solution>
              <Folder Name="/Solution Items/">
                <File Path="agents.json" />
                <File Path="README.md" />
                <File Path="MIGRATION.md" />
              </Folder>
              <Project Path="{projectNamespace}.csproj" />
            </Solution>
            """;

        // Both at the archive's root, beside the sources they build.
        return
        [
            new EmittedFile($"{projectNamespace}.csproj", csproj, FileOwnership.Generated),
            new EmittedFile($"{projectNamespace}.slnx", slnx, FileOwnership.Generated)
        ];
    }

    /// <summary>
    /// The version to pin a package reference to: the build of it this Alembic runs against, read off one of its own types.
    /// Restated by hand it would drift the day the framework ships a new one.
    /// </summary>
    private static string PackageVersion(Type typeFromThatPackage)
    {
        // The informational version is the package's own, pre-release suffix included: the numeric
        // assembly version would pin 1.0.0, which no pre-release package satisfies. The SDK appends the
        // source revision after a '+', which is no part of a package version.
        string? informationalVersion = typeFromThatPackage.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(informationalVersion) ? "*" : informationalVersion.Split('+')[0];
    }
}