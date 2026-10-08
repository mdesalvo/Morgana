using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Morgana.AI.Workflows;

/// <summary>
/// The function that starts one workflow: code recognises a launcher by this type, never by its name.
/// </summary>
public sealed class WorkflowLauncherFunction(string name, string description, Func<ValueTask<object?>> launch) : AIFunction
{
    /// <summary>The schema of a function taking no argument.</summary>
    private static readonly JsonElement NoArguments = JsonSerializer.SerializeToElement(
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

    /// <inheritdoc />
    public override string Name => name;

    /// <inheritdoc />
    public override string Description => description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => NoArguments;

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => launch();
}
