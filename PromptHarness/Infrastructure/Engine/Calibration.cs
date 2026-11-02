using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>A case of a calibration set: its id and whether the guard must admit it (<c>pass</c>) or withhold it (<c>fail</c>).</summary>
public interface ICalibrationCase
{
    /// <summary>Stable identifier, also the Theory argument that selects the case.</summary>
    string Id { get; }

    /// <summary>Either <c>pass</c> (the guard must admit the case) or <c>fail</c> (it must withhold it).</summary>
    string Expect { get; }
}

/// <summary>One ToolGuard calibration case: a tool result of a given form and whether the ToolGuard inspector must withhold it.</summary>
public sealed class ToolGuardCase : ICalibrationCase
{
    /// <summary>Stable identifier, also the Theory argument that selects the case.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Either <c>pass</c> (the inspector must admit the result) or <c>fail</c> (it must withhold it).</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>The form of the result under test, in one sentence.</summary>
    public string Form { get; init; } = string.Empty;

    /// <summary>How the result reaches the tool loop: <c>mcp-text</c>, <c>mcp-structured</c> or <c>partner</c>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name of the tool that answered.</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>The arguments the tool was called with, as JSON object text.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>What the tool returned: plain text or JSON text, according to <see cref="Source"/>.</summary>
    public string Result { get; init; } = string.Empty;
}

/// <summary>One ToolGuard calibration case on an offer: what a source offers and whether the ToolGuard inspector must refuse it.</summary>
public sealed class ToolGuardOfferCase : ICalibrationCase
{
    /// <summary>Stable identifier, also the Theory argument that selects the case.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Either <c>pass</c> (the inspector must admit the offer) or <c>fail</c> (it must refuse it).</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>The form of the offer under test, in one sentence.</summary>
    public string Form { get; init; } = string.Empty;

    /// <summary>Who makes the offer: <c>mcp</c> (a server's whole listing) or <c>partner</c> (the description on a card).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name of the MCP server or of the partner that makes the offer.</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>The intent of the partner's colleague; null for an MCP server.</summary>
    public string? Intent { get; init; }

    /// <summary>The partner that publishes the colleague; null for an MCP server.</summary>
    public string? Instance { get; init; }

    /// <summary>The tools on offer.</summary>
    public List<ToolGuardOfferedToolCase> Tools { get; init; } = [];
}

/// <summary>One tool of a calibration offer, as its source describes it.</summary>
public sealed class ToolGuardOfferedToolCase
{
    /// <summary>The name under which the model would call the tool.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The description the source gives the tool.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The tool's input schema as JSON text; empty for a partner's function.</summary>
    public string Parameters { get; init; } = string.Empty;
}

/// <summary>One UserGuard calibration case: a message of a given form and whether the UserGuard must refuse it.</summary>
public sealed class UserGuardCase : ICalibrationCase
{
    /// <summary>Stable identifier, also the Theory argument that selects the case.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Either <c>pass</c> (the UserGuard must admit the message) or <c>fail</c> (it must refuse it).</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>The form of the message under test, in one sentence.</summary>
    public string Form { get; init; } = string.Empty;

    /// <summary>What the user wrote.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The answer the user received before writing; null when the message opens the conversation.</summary>
    public UserGuardCaseAnswer? Answer { get; init; }
}

/// <summary>The answer a UserGuard calibration case says the user received before writing.</summary>
public sealed class UserGuardCaseAnswer
{
    /// <summary>The text of the answer.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The rich card the answer carried, as JSON text; null when there was none.</summary>
    public string? Card { get; init; }

    /// <summary>The labels of the buttons the answer carried; null when there were none.</summary>
    public List<string>? Buttons { get; init; }
}

/// <summary>One PeerGuard calibration case: a partner's question of a given form and whether the PeerGuard must decline it.</summary>
public sealed class PeerGuardCase : ICalibrationCase
{
    /// <summary>Stable identifier, also the Theory argument that selects the case.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Either <c>pass</c> (the PeerGuard must admit the question) or <c>fail</c> (it must decline it).</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>The form of the question under test, in one sentence.</summary>
    public string Form { get; init; } = string.Empty;

    /// <summary>The partner asking.</summary>
    public string Partner { get; init; } = string.Empty;

    /// <summary>The agent asked.</summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>What the agent answers for, as its card publishes it.</summary>
    public string Territory { get; init; } = string.Empty;

    /// <summary>What the partner asked.</summary>
    public string Question { get; init; } = string.Empty;
}

/// <summary>A calibration file as a whole: the list of cases.</summary>
/// <typeparam name="TCase">The kind of case the file holds.</typeparam>
public sealed class CalibrationFile<TCase>
{
    /// <summary>Every case of the calibration set.</summary>
    public List<TCase> Cases { get; init; } = [];
}

/// <summary>Loads the cases of a calibration set from the YAML files copied beside the assembly.</summary>
public static class CalibrationSet
{
    /// <summary>Shared deserializer: YAML keys are camelCase, matching the property names.</summary>
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The parsed files keyed by file name: each file is read once per process.</summary>
    private static readonly ConcurrentDictionary<string, object> Files = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

    /// <summary>Returns the case with the given id from the given calibration file.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the calibration file holds no such case.</exception>
    public static TCase Load<TCase>(string fileName, string caseId) where TCase : ICalibrationCase
    {
        // Theory cases of one class run in parallel threads of the same process: the first reader parses and the others share the result.
        CalibrationFile<TCase> calibrationFile = (CalibrationFile<TCase>)Files.GetOrAdd(
            fileName,
            name => Deserializer.Deserialize<CalibrationFile<TCase>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Calibration", name))));

        // A typo in an [InlineData] id fails here by name instead of running against nothing.
        foreach (TCase candidate in calibrationFile.Cases)
            if (string.Equals(candidate.Id, caseId, StringComparison.Ordinal))
                // The case is handed to the test as the file declares it.
                return candidate;

        // No case carries the id: the file and the Theory disagree.
        throw new InvalidOperationException($"Calibration case '{caseId}' not found in '{fileName}'.");
    }
}

/// <summary>Turns a ToolGuard calibration case into the text the inspector is handed.</summary>
public static class ToolGuardCalibration
{
    /// <summary>Serializes the result exactly as the guard's middleware hands it to the guard.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the case names a source that is not one of the three forms.</exception>
    public static string SerializeResult(ToolGuardCase toolGuardCase)
    {
        // The shape of the result as the tool loop holds it before the middleware serializes it.
        object? result;

        switch (toolGuardCase.Source)
        {
            case "mcp-text":
                // An MCP tool answering plain text arrives as a text content.
                result = new TextContent(toolGuardCase.Result);
                break;

            case "mcp-structured":
            {
                // An MCP tool answering a record arrives as the whole call result: the record both in structuredContent and as text.
                JsonElement structuredContent = JsonSerializer.Deserialize<JsonElement>(toolGuardCase.Result, AIJsonUtilities.DefaultOptions);
                string compactText = JsonSerializer.Serialize(structuredContent, AIJsonUtilities.DefaultOptions);
                Dictionary<string, object?> callResult = new Dictionary<string, object?>
                {
                    ["content"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = compactText } },
                    ["structuredContent"] = structuredContent,
                    ["resultType"] = "complete"
                };
                result = JsonSerializer.SerializeToElement(callResult, AIJsonUtilities.DefaultOptions);
                break;
            }

            case "partner":
                // A partner's answer reaches the tool loop as text.
                result = toolGuardCase.Result;
                break;

            default:
                // A source the harness does not model would measure the wrong form silently.
                throw new InvalidOperationException($"ToolGuard calibration case '{toolGuardCase.Id}' names an unknown source '{toolGuardCase.Source}'.");
        }

        // The middleware serializes with these options before handing the text to the guard.
        return JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
    }

    /// <summary>Serializes the arguments the way the middleware hands the name-value pairs to the guard.</summary>
    public static string SerializeArguments(ToolGuardCase toolGuardCase)
    {
        // Parsed and written again so the guard reads compact JSON whatever the layout in the YAML.
        JsonElement arguments = JsonSerializer.Deserialize<JsonElement>(toolGuardCase.Arguments, AIJsonUtilities.DefaultOptions);
        return JsonSerializer.Serialize(arguments, AIJsonUtilities.DefaultOptions);
    }

    /// <summary>Builds the tools an MCP server lists for an offer case, each carrying only its name, description and input schema.</summary>
    public static IList<AIFunction> OfferedTools(ToolGuardOfferCase offerCase)
    {
        // The tools in the order the server lists them.
        List<AIFunction> offeredTools = [];

        // Each tool of the case becomes one listed tool.
        foreach (ToolGuardOfferedToolCase toolCase in offerCase.Tools)
        {
            // A tool that declares no schema takes no arguments.
            string schemaText = string.IsNullOrWhiteSpace(toolCase.Parameters) ? "{\"type\":\"object\",\"properties\":{}}" : toolCase.Parameters;

            // The schema as the server sent it.
            JsonElement schema = JsonSerializer.Deserialize<JsonElement>(schemaText, AIJsonUtilities.DefaultOptions);
            offeredTools.Add(new OfferedTool(toolCase.Name, toolCase.Description, schema));
        }

        // The listing the scripted server answers with.
        return offeredTools;
    }
}
