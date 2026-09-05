using System.Reflection;

namespace PromptHarness.Infrastructure;

/// <summary>
/// What one agent's Draft promises the emitted assembly will contain: a <c>MorganaAgent</c>
/// subclass handling its intent and — where it declares tools — a <c>MorganaTool</c> subclass
/// providing them, one public method per declared tool.
/// </summary>
public sealed record AgentExpectation(string IntentName, IReadOnlyList<string> ToolNames);

/// <summary>
/// Reflects over the assembly <see cref="ArchiveCompiler"/> just built and checks it actually
/// declares what the Draft promised.
/// </summary>
/// <remarks>
/// A clean <c>dotnet build</c> proves the generated <c>.g.cs</c> and the model-authored mock agree
/// with each other and with Morgana.AI's own types — it proves nothing about whether the result is
/// the SPECIFIC agent and toolkit this domain's Draft actually asked for. A tool class built against
/// the wrong intent name, or a partial method quietly never implemented because the client's half
/// happened to compile without it, is invisible to the compiler and would still read as
/// "Emitted_archive_compiles_clean: passed." This is the harness's own version of the two checks
/// that stand between "this built" and "this runs": <c>HandlesIntentAgentRegistryService</c> and
/// <c>MorganaToolAdapter.AddTool</c> at a real Morgana's startup, done here without booting one.
/// </remarks>
public static class EmittedAssemblyInspector
{
    private const string AgentBaseType = "Morgana.AI.Abstractions.MorganaAgent";
    private const string ToolBaseType = "Morgana.AI.Abstractions.MorganaTool";
    private const string HandlesIntentAttribute = "Morgana.AI.Attributes.HandlesIntentAttribute";
    private const string ProvidesToolForIntentAttribute = "Morgana.AI.Attributes.ProvidesToolForIntentAttribute";
    private const string RequiresLLMTierAttribute = "Morgana.AI.Attributes.RequiresLLMTierAttribute";

    /// <summary>
    /// Everything <paramref name="expectations"/> promised that the assembly at
    /// <paramref name="assemblyPath"/> does not actually declare — empty when it matches.
    /// </summary>
    public static IReadOnlyList<string> FindMissing(string assemblyPath, IReadOnlyList<AgentExpectation> expectations)
    {
        List<string> missing = [];
        Type[] types = LoadTypes(assemblyPath);

        foreach (AgentExpectation expectation in expectations)
        {
            Type? agentType = types.FirstOrDefault(t =>
                DerivesFrom(t, AgentBaseType) && IntentOf(t, HandlesIntentAttribute) == expectation.IntentName);

            if (agentType is null)
            {
                missing.Add($"No MorganaAgent subclass carries [HandlesIntent(\"{expectation.IntentName}\")].");
                continue;
            }

            // Every emitted agent carries this — CodeEmitService.EmitAgent writes it unconditionally,
            // defaulting to Efficiency when the Draft's own Code.Tier is unset — so its absence is
            // never a legitimate domain choice. MorganaAgentAdapter reads it to pick which LLMService
            // tier the agent's constructor is handed; missing, that fails at Morgana's own startup
            // exactly the way an unresolved base class or intent would.
            if (!HasAttribute(agentType, RequiresLLMTierAttribute))
                missing.Add($"'{expectation.IntentName}' agent class has no [RequiresLLMTier] attribute.");

            if (expectation.ToolNames.Count == 0)
                continue;

            Type? toolType = types.FirstOrDefault(t =>
                DerivesFrom(t, ToolBaseType) && IntentOf(t, ProvidesToolForIntentAttribute) == expectation.IntentName);

            if (toolType is null)
            {
                missing.Add($"No MorganaTool subclass carries [ProvidesToolForIntent(\"{expectation.IntentName}\")].");
                continue;
            }

            HashSet<string> declaredMethods = [.. toolType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)];

            foreach (string toolName in expectation.ToolNames)
                if (!declaredMethods.Contains(toolName))
                    missing.Add($"'{expectation.IntentName}' tool class has no public method '{toolName}'.");
        }

        return missing;
    }

    /// <summary>
    /// Loads the assembly and its exported types, tolerating a dependency that fails to resolve
    /// rather than losing the whole inspection to one — the same partial-success handling
    /// <c>Type.GetType</c> callers reach for against <see cref="ReflectionTypeLoadException"/>.
    /// </summary>
    private static Type[] LoadTypes(string assemblyPath)
    {
        Assembly assembly = Assembly.LoadFrom(assemblyPath);

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(t => t is not null)!];
        }
    }

    /// <summary>
    /// Walks a type's base-class chain by <see cref="Type.FullName"/> rather than
    /// <see cref="Type.IsAssignableFrom"/>: <see cref="Assembly.LoadFrom(string)"/> loads its own
    /// copy of Morgana.AI, which can land in a different load context than the one this very test
    /// process already has loaded — comparing by name never has an identity mismatch to get wrong.
    /// </summary>
    private static bool DerivesFrom(Type type, string baseTypeFullName)
    {
        for (Type? cursor = type.BaseType; cursor is not null; cursor = cursor.BaseType)
            if (cursor.FullName == baseTypeFullName)
                return true;

        return false;
    }

    /// <summary>
    /// The named attribute's <c>Intent</c> constructor argument, read from
    /// <see cref="CustomAttributeData"/> — metadata only, so nothing here ever constructs the
    /// attribute or needs its type loaded into this process.
    /// </summary>
    private static string? IntentOf(Type type, string attributeFullName)
    {
        CustomAttributeData? data = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == attributeFullName);

        return data is { ConstructorArguments.Count: > 0 }
            ? data.ConstructorArguments[0].Value as string
            : null;
    }

    /// <summary>Whether the named attribute decorates <paramref name="type"/> at all — metadata
    /// only, same reasoning as <see cref="IntentOf"/>, for an attribute this only needs to see.</summary>
    private static bool HasAttribute(Type type, string attributeFullName) =>
        type.GetCustomAttributesData().Any(a => a.AttributeType.FullName == attributeFullName);
}