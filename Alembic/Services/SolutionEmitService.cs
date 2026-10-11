using Alembic.Interfaces;
using Alembic.Models;
using System.Reflection;

namespace Alembic.Services;

/// <summary>
/// Writes the project and solution files that house the emitted sources, so the unzipped archive builds as it is.
/// </summary>
/// <remarks>The build needs a NuGet feed that resolves Morgana.AI, which Alembic cannot see.</remarks>
public class SolutionEmitService : ISolutionEmitService
{
    /// <summary>The .NET that the emitted project targets, the one Alembic and Morgana.AI are built on.</summary>
    private const string TargetFramework = "net10.0";

    /// <inheritdoc />
    /// <returns>The <c>.csproj</c> and <c>.slnx</c> named after the namespace, both generated so a hand edit is lost on the next archive.</returns>
    public IReadOnlyList<EmittedFile> Emit(DomainDraft draft)
    {
        // Every agent of the domain is emitted into one namespace: the first that declares one stands for all.
        string ns = draft.Agents.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Code.Namespace))?.Code.Namespace
                    ?? CodeEmitService.DefaultNamespace;

        // The Morgana packages are pinned to the build that this Alembic runs against.
        string version = PackageVersion(typeof(Morgana.AI.Records));

        // The project is a library that embeds agents.json and references the two Morgana packages.
        string csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

                <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>{TargetFramework}</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <RootNamespace>{ns}</RootNamespace>
                    <AssemblyName>{ns}</AssemblyName>
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

        // The solution lists the project together with the documents that travel in the archive.
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

        // The archive receives the project and the solution as generated files.
        return
        [
            new EmittedFile($"{ns}.csproj", csproj, FileOwnership.Generated),
            new EmittedFile($"{ns}.slnx", slnx, FileOwnership.Generated)
        ];
    }

    /// <summary>The version of the package that a type comes from, which the emitted project pins its reference to.</summary>
    private static string PackageVersion(Type typeFromThatPackage)
    {
        // The informational version carries the preview suffix that the package is published with.
        // The source revision after the '+' is no part of a package version.
        string? informationalVersion = typeFromThatPackage.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // A build without version information accepts any version of the package.
        return string.IsNullOrEmpty(informationalVersion) ? "*" : informationalVersion.Split('+')[0];
    }
}
