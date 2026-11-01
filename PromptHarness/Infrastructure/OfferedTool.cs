using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PromptHarness.Infrastructure;

/// <summary>A tool as an MCP server offers it, carrying only its name, description and input schema.</summary>
public sealed class OfferedTool : AIFunction
{
    /// <summary>The input schema as the server sent it.</summary>
    private readonly JsonElement jsonSchema;

    /// <summary>Creates the tool the server describes.</summary>
    public OfferedTool(string name, string description, JsonElement jsonSchema)
    {
        // The tool carries exactly what the server announced and nothing else.
        Name = name;
        Description = description;
        this.jsonSchema = jsonSchema;
    }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    public override string Description { get; }

    /// <inheritdoc/>
    public override JsonElement JsonSchema => jsonSchema;

    /// <summary>An offered tool is never called in the harness.</summary>
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => throw new InvalidOperationException("an offered tool is never called in the harness");
}
