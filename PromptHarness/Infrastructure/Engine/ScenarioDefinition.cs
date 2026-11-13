using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// One non-regression scenario: a scripted conversation plus what must hold at each turn.
/// Scenarios are data, not code, because they encode prompt behaviour, which a domain author tunes most often.
/// </summary>
public sealed class ScenarioDefinition
{
    /// <summary>Stable identifier, also the file name, named in every failure message.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What the scenario protects in one sentence, read by people only.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>How many times to replay the conversation; <c>Harness:DefaultRuns</c> when unset.</summary>
    public int? Runs { get; init; }

    /// <summary>
    /// How many of those runs must pass; <c>Harness:DefaultMinPasses</c> when unset.
    /// The context-handling group sets it equal to <see cref="Runs"/>: a contract that holds four times out of five does not hold.
    /// </summary>
    public int? MinPasses { get; init; }

    /// <summary>The turns, in order.</summary>
    public List<TurnDefinition> Turns { get; init; } = [];

    /// <summary>
    /// Opens the conversation under <c>HarnessChannel.DegradedCapabilities</c> instead of the full-capability channel.
    /// It is the opt-in of the one scenario that exercises <c>MorganaChannelAdapter</c>'s rewrite path.
    /// </summary>
    public bool? DegradedChannel { get; init; }

    /// <summary>
    /// Customers interchangeable for a scenario whose action a customer may take only once, such as enrolling in a plan.
    /// The shop's state does not reset between runs as a session does, so each run's <c>{{customerCode}}</c> in <c>say:</c> becomes
    /// <c>CustomerCodes[(run - 1) % CustomerCodes.Count]</c>, reused round-robin when the runs outnumber the codes.
    /// </summary>
    public List<string>? CustomerCodes { get; init; }
}

/// <summary>A single user turn and its expectations.</summary>
public sealed class TurnDefinition
{
    /// <summary>What the user says; exactly one of <c>say:</c> and <c>press:</c> is set.</summary>
    public string Say { get; init; } = string.Empty;

    /// <summary>The exact id of a button on the last answer (a welcome or disambiguation button is an intent name) that this turn presses instead of typing.</summary>
    public string? Press { get; init; }

    /// <summary>The structural expectations, evaluated deterministically; optional.</summary>
    public ExpectSpec? Expect { get; init; }

    /// <summary>Propositions about what the user saw that the judge must find true; optional.</summary>
    public List<string>? Judge { get; init; }

    /// <summary>Propositions the judge must find false; optional.</summary>
    public List<string>? JudgeNot { get; init; }
}

/// <summary>Structural expectations on a turn, every property optional and only the ones set checked.</summary>
public sealed class ExpectSpec
{
    /// <summary>Expected <c>agentCompleted</c> on the delivered message.</summary>
    public bool? AgentCompleted { get; init; }

    /// <summary>Quick-reply cardinality: <c>none</c>, <c>any</c>, <c>count:N</c> or <c>min:N</c>.</summary>
    public string? QuickReplies { get; init; }

    /// <summary>Quick-reply ids that must all be present.</summary>
    public List<string>? QuickReplyIds { get; init; }

    /// <summary>The delivered buttons' labels, exactly and in order: the whole list must be equal.</summary>
    public List<string>? QuickReplyLabels { get; init; }

    /// <summary>Quick-reply ids that must all be absent.</summary>
    public List<string>? NoQuickReplyIds { get; init; }

    /// <summary>Rich card presence: <c>absent</c> or <c>present</c>.</summary>
    public string? RichCard { get; init; }

    /// <summary>
    /// Substrings that must appear in the rich card's text, as <see cref="RichCardText"/> renders it.
    /// It is for deterministic values only, such as the seal word that <c>Harness:DeterministicSealWord</c> pins, which a judge would read as wording.
    /// </summary>
    public List<string>? RichCardContains { get; init; }

    /// <summary>Tools that must appear among the turn's invocations.</summary>
    public List<string>? ToolsCalled { get; init; }

    /// <summary>
    /// Tools that must appear exactly once, which is what asking a colleague once looks like from outside.
    /// The invocation list keeps repetitions, so a second call in one turn is an agent going round again instead of owning the gap.
    /// </summary>
    public List<string>? ToolsCalledOnce { get; init; }

    /// <summary>Tools that must not appear.</summary>
    public List<string>? ToolsNotCalled { get; init; }

    /// <summary>
    /// Tools whose result must have been withheld in the turn on a verdict of the deterministic check, a model or the provider's filter.
    /// A result withheld because no verdict could be reached is no catch and never satisfies it.
    /// </summary>
    public List<string>? ToolsQuarantined { get; init; }

    /// <summary>Tools that must have been called in the turn with no result of theirs withheld.</summary>
    public List<string>? ToolsAdmitted { get; init; }

    /// <summary>
    /// Doors (<c>mcp_offer</c> or <c>partner_card</c>) at which an offer must have been refused on a verdict
    /// since the process started: an offer is examined at startup and belongs to no turn.
    /// </summary>
    public List<string>? OffersRefused { get; init; }

    /// <summary>Tools the turn must open with, in this order, before anything else it calls.</summary>
    public List<string>? ToolsCalledFirst { get; init; }

    /// <summary>
    /// Context variables the turn must have read: through <c>GetContextVariable</c> (<c>Hit</c>, <c>Miss</c>) or the held-context declaration (<c>Declared</c>).
    /// A bare name matches any of the three and a prefixed one (<c>Hit:customerCode</c>) only that form; a shared variable hydrated for a follow-up
    /// agent is normally <c>Declared</c>, since the agent already has the value in front of it.
    /// </summary>
    public List<string>? ContextReads { get; init; }

    /// <summary>Context variables that must have been written via <c>SetContextVariable</c>.</summary>
    public List<string>? ContextWrites { get; init; }

    /// <summary>Asserts that no context write happened at all.</summary>
    public bool? NoContextWrites { get; init; }

    /// <summary>
    /// Asserts that the turn neither read nor wrote any context variable.
    /// It is the closed vocabulary of an agent declaring no context parameter, for which any access is an invention.
    /// </summary>
    public bool? NoContextAccess { get; init; }

    /// <summary>The closed vocabulary every context name touched on this turn must belong to, so a name derived from the user's message fails it.</summary>
    public List<string>? ContextVocabulary { get; init; }

    /// <summary>Requires a non-empty response text, as the <c>MandatoryTextualResponse</c> policy does.</summary>
    public bool? TextNotEmpty { get; init; }

    /// <summary>Substrings that must not occur in the response text, case-insensitively.</summary>
    public List<string>? TextNotContains { get; init; }

    /// <summary>Expected agent class name, such as <c>BillingAgent</c> or <c>none</c> for a turn no agent took.</summary>
    public string? Agent { get; init; }

    /// <summary>Expected <c>guard.compliant</c> verdict from the <c>morgana.guard</c> span.</summary>
    public bool? GuardCompliant { get; init; }

    /// <summary>Whether a <c>morgana.classifier</c> span was seen in the turn: false for a landing or a follow-up turn.</summary>
    public bool? Classified { get; init; }

    /// <summary>Expected intent from the <c>morgana.classifier</c> span.</summary>
    public string? ClassifierIntent { get; init; }

    /// <summary>Minimum acceptable confidence from the same span.</summary>
    public double? ClassifierMinConfidence { get; init; }

    /// <summary>Maximum allowed length of the delivered text, in characters.</summary>
    public int? TextMaxLength { get; init; }

    /// <summary>Asserts that the delivered text carries no Markdown syntax.</summary>
    public bool? TextNotMarkdown { get; init; }

    /// <summary>
    /// Agents, by intent, that must own no message in the persisted history after this turn, matched on the history's <c>Morgana (Billing)</c> attribution.
    /// It proves that a consulted colleague answered and left no record of having spoken.
    /// </summary>
    public List<string>? HistoryExcludesAgents { get; init; }

    /// <summary>
    /// The exact number of user messages the persisted history must carry after this turn, one per turn played.
    /// A colleague's question is stored with the user role, so a consultation that left a trace shows up as a message the person never sent.
    /// </summary>
    public int? HistoryUserMessages { get; init; }

    /// <summary>
    /// Asserts that the history reducer shrank the conversation before this turn, read from <c>MorganaChatHistoryProvider</c>'s log line since no span carries it.
    /// It needs a run under <c>SummarizationTests</c>, whose lowered thresholds are what make a scripted conversation cross the trigger.
    /// </summary>
    public bool? SummarizationOccurred { get; init; }
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

    /// <summary>Loads one scenario by id, its file name without extension, refusing a file that acts wrongly before any paid run.</summary>
    public static ScenarioDefinition Load(string id)
    {
        // The id is the file name, so a typo in an [InlineData] id fails here by name instead of running the wrong expectations.
        string path = Path.Combine(Directory, $"{id}.yaml");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Scenario '{id}' not found at {path}.", path);

        // The YAML read into the scenario's shape, its keys camelCase.
        ScenarioDefinition scenario = Deserializer.Deserialize<ScenarioDefinition>(File.ReadAllText(path));

        // Every failure message and every journey row keys off the id, so a file that parses without "id:" is refused at load.
        if (string.IsNullOrWhiteSpace(scenario.Id))
            throw new InvalidOperationException($"Scenario file {path} declares no id.");

        // A turn that says and presses acts twice and one that does neither acts not at all: both are a typo caught before any paid run.
        for (int turnIndex = 0; turnIndex < scenario.Turns.Count; turnIndex++)
        {
            bool says = !string.IsNullOrWhiteSpace(scenario.Turns[turnIndex].Say);
            bool presses = !string.IsNullOrWhiteSpace(scenario.Turns[turnIndex].Press);

            // Both or neither: the turn number tells the author where to look.
            if (says == presses)
                throw new InvalidOperationException($"Scenario file {path}: turn {turnIndex + 1} must declare exactly one of 'say' and 'press'.");
        }

        // A scenario whose every turn acts exactly once.
        return scenario;
    }
}