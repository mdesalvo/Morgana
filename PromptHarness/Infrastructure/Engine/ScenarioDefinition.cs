using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// One non-regression scenario: a scripted conversation plus what must hold at each turn.
/// </summary>
/// <remarks>
/// Scenarios are data, not code, because what they encode is prompt behaviour — the layer that
/// changes most often and that a domain author, not a framework developer, is expected to tune.
/// </remarks>
public sealed class ScenarioDefinition
{
    /// <summary>Stable identifier, also the file name. Appears in every failure message.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What the scenario is protecting, in one sentence. Read by humans only.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>How many times to replay the conversation. Falls back to <c>Harness:DefaultRuns</c>.</summary>
    public int? Runs { get; init; }

    /// <summary>
    /// How many of those runs must pass. Falls back to <c>Harness:DefaultMinPasses</c>. The
    /// context-handling group sets this equal to <see cref="Runs"/>: its properties are contract,
    /// and a contract that holds four times out of five does not hold.
    /// </summary>
    public int? MinPasses { get; init; }

    /// <summary>The turns, in order.</summary>
    public List<TurnDefinition> Turns { get; init; } = [];
}

/// <summary>A single user turn and its expectations.</summary>
public sealed class TurnDefinition
{
    /// <summary>What the user says.</summary>
    public string Say { get; init; } = string.Empty;

    /// <summary>Structural expectations, evaluated deterministically. Optional.</summary>
    public ExpectSpec? Expect { get; init; }

    /// <summary>Propositions about the response text that an LLM judge must find true. Optional.</summary>
    public List<string>? Judge { get; init; }

    /// <summary>Propositions the judge must find false. Optional.</summary>
    public List<string>? JudgeNot { get; init; }
}

/// <summary>
/// Structural expectations on a turn. Every property is optional; only the ones set are checked.
/// </summary>
public sealed class ExpectSpec
{
    /// <summary>Expected <c>agentCompleted</c> on the delivered message.</summary>
    public bool? AgentCompleted { get; init; }

    /// <summary>Quick-reply cardinality: <c>none</c>, <c>any</c>, <c>count:N</c> or <c>min:N</c>.</summary>
    public string? QuickReplies { get; init; }

    /// <summary>Quick-reply ids that must all be present.</summary>
    public List<string>? QuickReplyIds { get; init; }

    /// <summary>Quick-reply ids that must all be absent.</summary>
    public List<string>? NoQuickReplyIds { get; init; }

    /// <summary>
    /// Assert that the escape options are not the entire emission — the precondition
    /// <c>QuickReplyEscapeOptions</c> states in so many words: they are "an APPENDIX, never a
    /// standalone emission", valid only alongside at least one primary option.
    /// </summary>
    /// <remarks>
    /// Opt-in, and deliberately so: a turn that has genuinely concluded emits exactly those two
    /// buttons and nothing else, which is <c>ConversationClosure</c> working as intended. The
    /// assertion belongs on turns that declare continuation, where a standalone escape pair would
    /// gate a lawful in-progress step behind an exit button.
    /// </remarks>
    public bool? NoStandaloneEscapeOptions { get; init; }

    /// <summary>
    /// The other half of the same rule: whenever the turn emits primary options, the escape pair
    /// must be appended to them, so the user is never trapped inside a list of choices.
    /// </summary>
    public bool? EscapeOptionsWithPrimary { get; init; }

    /// <summary>Rich card presence: <c>absent</c> or <c>present</c>.</summary>
    public string? RichCard { get; init; }

    /// <summary>Tools that must appear among the turn's invocations.</summary>
    public List<string>? ToolsCalled { get; init; }

    /// <summary>Tools that must not appear.</summary>
    public List<string>? ToolsNotCalled { get; init; }

    /// <summary>
    /// Tools that must be called <em>before</em> the agent produces its answer for the first time —
    /// expressed as a prefix constraint on the invocation order.
    /// </summary>
    public List<string>? ToolsCalledFirst { get; init; }

    /// <summary>Context variables that must have been read via <c>GetContextVariable</c>.</summary>
    public List<string>? ContextReads { get; init; }

    /// <summary>Context variables that must have been written via <c>SetContextVariable</c>.</summary>
    public List<string>? ContextWrites { get; init; }

    /// <summary>Assert that no context write happened at all.</summary>
    public bool? NoContextWrites { get; init; }

    /// <summary>
    /// Assert that the turn touched the context registry not at all — neither read nor write. The
    /// form the closed vocabulary takes for an agent that declares no <c>Scope: context</c>
    /// parameter: there, every legal name is no name, so any access is an invention.
    /// </summary>
    public bool? NoContextAccess { get; init; }

    /// <summary>
    /// The closed vocabulary: every context name touched on this turn must appear here. This is the
    /// anti-invention assertion — a name derived from the user's message fails it.
    /// </summary>
    public List<string>? ContextVocabulary { get; init; }

    /// <summary>Require a non-empty response text (the <c>MandatoryTextualResponse</c> policy).</summary>
    public bool? TextNotEmpty { get; init; }

    /// <summary>Substrings that must not occur in the response text, case-insensitively.</summary>
    public List<string>? TextNotContains { get; init; }

    /// <summary>Expected agent class name, e.g. <c>BillingAgent</c>.</summary>
    public string? Agent { get; init; }
}

/// <summary>Loads scenario files from the harness output directory.</summary>
public static class ScenarioLoader
{
    /// <summary>Shared deserializer: YAML keys are camelCase, matching the property names.</summary>
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Directory holding the scenario files, alongside the test assembly.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Scenarios");

    /// <summary>Loads one scenario by id (file name without extension).</summary>
    public static ScenarioDefinition Load(string id)
    {
        string path = Path.Combine(Directory, $"{id}.yaml");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Scenario '{id}' not found at {path}.", path);

        ScenarioDefinition scenario = Deserializer.Deserialize<ScenarioDefinition>(File.ReadAllText(path));

        return string.IsNullOrWhiteSpace(scenario.Id) ? throw new InvalidOperationException($"Scenario file {path} declares no id.") : scenario;
    }
}
