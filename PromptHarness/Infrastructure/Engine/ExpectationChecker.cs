using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Morgana.AI;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// Evaluates a turn's structural expectations, deterministically.
/// Everything it reads is on the delivered <see cref="ChannelMessage"/> or among the span and log signals the <see cref="TurnObserver"/> collected,
/// never in the wording of the response.
/// </summary>
public static partial class ExpectationChecker
{
    /// <summary>
    /// Matches <c>MorganaChatHistoryProvider</c>'s per-turn log line of a reduced view.
    /// It is written every turn a reducer is configured, so only its two counts tell a reduction from a reducer that kept everything.
    /// </summary>
    [GeneratedRegex(@"PROVIDING reduced view \((?<full>\d+) → (?<reduced>\d+) messages\)", RegexOptions.CultureInvariant)]
    private static partial Regex SummarizationLogPattern { get; }

    /// <summary>Returns one message per violated expectation; empty when the turn conforms.</summary>
    /// <param name="history">The persisted history, fetched only for a turn that declares an expectation on it, since it costs a round trip to the host.</param>
    public static IReadOnlyList<string> Check(ExpectSpec expect, TurnResult turn, IReadOnlyList<MorganaChatMessage>? history = null)
    {
        // Every violated expectation of the turn, reported together.
        List<string> failures = [];

        // An expectation left unset is one the scenario does not make, so only a stated one that the turn contradicts adds a message.
        if (expect.AgentCompleted is { } expectedCompleted && turn.Message.AgentCompleted != expectedCompleted)
            failures.Add($"agentCompleted: expected {expectedCompleted}, got {turn.Message.AgentCompleted}");

        // "none" is a turn that Morgana answered herself, with no morgana.agent span: an intent no agent handles.
        if (string.Equals(expect.Agent, "none", StringComparison.OrdinalIgnoreCase))
        {
            // Any agent span at all contradicts it.
            if (turn.AgentName is not null)
                failures.Add($"agent: expected none, got {turn.AgentName}");
        }
        // A named agent must be the one whose span the turn carried.
        else if (expect.Agent is { Length: > 0 } expectedAgent
            && !string.Equals(turn.AgentName, expectedAgent, StringComparison.OrdinalIgnoreCase))
            failures.Add($"agent: expected {expectedAgent}, got {turn.AgentName ?? "(none)"}");

        // One method per concern, every one run, so a scenario touching several reports every failure in one pass.
        CheckQuickReplies(expect, turn, failures);
        CheckRichCard(expect, turn, failures);
        CheckRichCardContains(expect, turn, failures);
        CheckTools(expect, turn, failures);
        CheckContext(expect, turn, failures);
        CheckText(expect, turn, failures);
        CheckGuard(expect, turn, failures);
        CheckGuardEvidence(turn, failures);
        CheckQuarantine(expect, turn, failures);
        CheckClassifier(expect, turn, failures);
        CheckSummarization(expect, turn, failures);
        CheckHistory(expect, history, failures);

        // The scenario's verdict on the turn: it passes when nothing was added.
        return failures;
    }

    /// <summary>What the conversation's persisted history does and does not carry.</summary>
    private static void CheckHistory(ExpectSpec expect, IReadOnlyList<MorganaChatMessage>? history, List<string> failures)
    {
        // A turn declaring nothing about the history has nothing to check here.
        if (expect.HistoryExcludesAgents is not { Count: > 0 } && expect.HistoryUserMessages is null)
            return;

        // A null history here means the runner was never asked for one, which can only happen if a
        // check above was added without teaching it to fetch — reported rather than passed silently.
        if (history is null)
        {
            failures.Add("history: expectation declared but no history was fetched for this turn");
            return;
        }

        // Each agent named must own no message: a consulted colleague answers without leaving a trace.
        foreach (string agent in expect.HistoryExcludesAgents ?? [])
        {
            // Matched on the parenthesised intent the persistence layer appends to the display name,
            // so an agent's own messages are distinguished from the framework's unqualified "Morgana".
            string attribution = $"({agent})";
            int owned = history.Count(message => message.AgentName.Contains(attribution, StringComparison.OrdinalIgnoreCase));

            // Even one message means the agent spoke in a conversation it was only consulted for.
            if (owned > 0)
                failures.Add($"historyExcludesAgents: '{agent}' owns {owned} message(s) in the persisted history");
        }

        // One user message per turn played: a colleague's question stored with the user role would show up as one more.
        if (expect.HistoryUserMessages is { } expectedUserMessages)
        {
            int actual = history.Count(message => message.Type == ChatMessageType.User);

            // A different count is a message the person never sent or one that was lost.
            if (actual != expectedUserMessages)
                failures.Add($"historyUserMessages: expected {expectedUserMessages}, got {actual}");
        }
    }

    /// <summary>Verdict of the <c>morgana.guard</c> span.</summary>
    private static void CheckGuard(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // The verdict itself; a turn with no guard span says so, since a guard switched off writes none.
        if (expect.GuardCompliant is { } expectedCompliant && turn.GuardCompliant != expectedCompliant)
            failures.Add($"guardCompliant: expected {expectedCompliant}, got {turn.GuardCompliant?.ToString() ?? "(no guard span — is Harness:EnableUserGuardrail on?)"}");

        // An asserted verdict counts only when a judgment decided it: an admission by default or a guard switched off says nothing about the text.
        if (expect.GuardCompliant is { } assertedCompliant && turn.GuardCompliant is not null)
        {
            // An admission must come from a model's own reading, since the deterministic check only ever refuses.
            if (assertedCompliant && !string.Equals(turn.GuardSource, Records.GuardVerdictSource.Inspector.ToString(), StringComparison.Ordinal))
                failures.Add($"guardCompliant: true must come from {Records.GuardVerdictSource.Inspector}, got {turn.GuardSource ?? "(no source)"}");

            // A refusal may come from any layer that judged.
            else if (!assertedCompliant && !IsJudgement(turn.GuardSource))
                failures.Add($"guardCompliant: false must come from a judgment ({Records.GuardVerdictSource.Prefilter}, {Records.GuardVerdictSource.Inspector} or {Records.GuardVerdictSource.ProviderFilter}), got {turn.GuardSource ?? "(no source)"}");
        }
    }

    /// <summary>Whether the layer that decided a guard verdict is one that judged: a model, the provider's filter or the deterministic check.</summary>
    private static bool IsJudgement(string? source)
        => source is not null
            && (string.Equals(source, Records.GuardVerdictSource.Prefilter.ToString(), StringComparison.Ordinal)
                || string.Equals(source, Records.GuardVerdictSource.Inspector.ToString(), StringComparison.Ordinal)
                || string.Equals(source, Records.GuardVerdictSource.ProviderFilter.ToString(), StringComparison.Ordinal));

    /// <summary>The rules every turn answers to without declaring them: a verdict counts only when a judgment decided it and a guard that could not judge proves nothing.</summary>
    private static void CheckGuardEvidence(TurnResult turn, List<string> failures)
    {
        // A guard that refused because it could not decide leaves the turn unjudged.
        if (string.Equals(turn.GuardSource, Records.GuardVerdictSource.FailClosed.ToString(), StringComparison.Ordinal))
            failures.Add($"guard: the verdict came from {Records.GuardVerdictSource.FailClosed} ({turn.GuardErrorType ?? "no cause"}) and a turn whose guard could not judge proves nothing");

        // The same failure at any other door of the turn: a tool result or an offer refused unjudged.
        foreach (SecurityEventObservation securityEvent in turn.TurnSecurityEvents)
        {
            if (!string.Equals(securityEvent.Door, Telemetry.SecurityDoors.User, StringComparison.Ordinal)
                && string.Equals(securityEvent.Source, nameof(Records.GuardVerdictSource.FailClosed), StringComparison.Ordinal))
                failures.Add($"security: the {securityEvent.Door} door could not judge ({Records.GuardVerdictSource.FailClosed}, {securityEvent.ErrorType ?? "no cause"}) and a turn whose guard could not judge proves nothing");
        }
    }

    /// <summary>The tools the turn had withheld on a verdict or admitted untouched and the offers refused at any point since startup.</summary>
    private static void CheckQuarantine(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // Each tool the scenario declares quarantined must have been caught in this turn.
        foreach (string tool in expect.ToolsQuarantined ?? [])
        {
            // What the framework recorded for this tool in the turn, which is also what a failure has to show.
            List<WithheldToolObservation> observations = [.. turn.WithheldTools.Where(observation => string.Equals(observation.ToolName, tool, StringComparison.OrdinalIgnoreCase))];

            // A catch is a result withheld on a verdict: one withheld with no verdict is the guard failing closed.
            bool hasVerdictQuarantine = observations.Any(observation =>
                string.Equals(observation.Outcome, Telemetry.SecurityOutcomes.Withheld, StringComparison.Ordinal) && IsJudgement(observation.Source));

            // The failure shows what was recorded instead, so a result withheld with no verdict reads as such.
            if (!hasVerdictQuarantine)
                failures.Add($"toolsQuarantined: '{tool}' was not withheld on a verdict (observed {(observations.Count == 0 ? "no withheld result" : string.Join(", ", observations.Select(observation => $"{observation.Outcome} by {observation.Source ?? "no verdict"}")))})");
        }

        // Each tool the scenario declares admitted must have reached the model in this turn.
        foreach (string tool in expect.ToolsAdmitted ?? [])
        {
            // Called and untouched: a tool never called was not admitted and one withheld was not admitted either.
            bool hasInvocation = turn.ToolsInvoked.Contains(tool, StringComparer.OrdinalIgnoreCase);
            int withheldCount = turn.WithheldTools.Count(observation => string.Equals(observation.ToolName, tool, StringComparison.OrdinalIgnoreCase));

            // The failure says which of the two conditions broke.
            if (!hasInvocation || withheldCount > 0)
                failures.Add($"toolsAdmitted: '{tool}' {(hasInvocation ? $"had {withheldCount} withheld result(s)" : "was not invoked")} (got {FormatList(turn.ToolsInvoked)})");
        }

        // Each door the scenario names must have refused an offer on a verdict.
        foreach (string door in expect.OffersRefused ?? [])
        {
            // The whole process is read because an offer is examined at startup and no turn owns that event.
            bool hasVerdictRefusal = turn.AllSecurityEvents.Any(securityEvent =>
                string.Equals(securityEvent.Door, door, StringComparison.Ordinal)
                && string.Equals(securityEvent.Outcome, Telemetry.SecurityOutcomes.Refused, StringComparison.Ordinal)
                && IsJudgement(securityEvent.Source));

            // The failure lists every security event seen, so a refusal decided by no verdict reads as such.
            if (!hasVerdictRefusal)
                failures.Add($"offersRefused: no offer was refused on a verdict at the {door} door (observed {(turn.AllSecurityEvents.Count == 0 ? "no security event" : string.Join(", ", turn.AllSecurityEvents.Select(securityEvent => $"{securityEvent.Door}/{securityEvent.Outcome}/{securityEvent.Source ?? "none"}")))})");
        }
    }

    /// <summary>Intent and confidence of the <c>morgana.classifier</c> span.</summary>
    private static void CheckClassifier(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // A classifier span carries the intent, so an intent-less turn is one the classifier never ran for.
        if (expect.Classified is { } expectedClassified && (turn.ClassifierIntent is not null) != expectedClassified)
            failures.Add($"classified: expected {expectedClassified}, got {turn.ClassifierIntent is not null}");

        // The intent the classifier chose; a follow-up turn has none, since it skips classification.
        if (expect.ClassifierIntent is { Length: > 0 } expectedIntent
            && !string.Equals(turn.ClassifierIntent, expectedIntent, StringComparison.OrdinalIgnoreCase))
            failures.Add($"classifierIntent: expected {expectedIntent}, got {turn.ClassifierIntent ?? "(none — was this a follow-up turn?)"}");

        // A confidence below the floor fails, as does a missing one: a correct intent chosen half-heartedly is a classifier near its edge.
        if (expect.ClassifierMinConfidence is { } minimumConfidence
            && (turn.ClassifierConfidence is not { } actualConfidence || actualConfidence < minimumConfidence))
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"classifierMinConfidence: expected at least {minimumConfidence:F2}, got {turn.ClassifierConfidence?.ToString("F2", CultureInfo.InvariantCulture) ?? "(none)"}"));
    }

    /// <summary>
    /// Whether <c>MorganaChatHistoryProvider</c>'s reducer shrank the conversation before this turn.
    /// The log line is written every turn a reducer is configured, so a reduction means the two counts of the last such line differ.
    /// </summary>
    private static void CheckSummarization(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // A turn declaring nothing about the reducer has nothing to check here.
        if (expect.SummarizationOccurred is not { } expectedOccurred)
            return;

        // The last line of the turn decides: each run of the agent's tool loop reads the history again: the latest
        // read is what the model saw.
        Match? match = null;
        foreach (string line in turn.LogLines)
        {
            Match candidate = SummarizationLogPattern.Match(line);

            // A later reduced-view line replaces an earlier one.
            if (candidate.Success)
                match = candidate;
        }

        // A reduction handed the model fewer messages than the history holds.
        bool occurred = match is not null
            && int.Parse(match.Groups["full"].Value, CultureInfo.InvariantCulture) > int.Parse(match.Groups["reduced"].Value, CultureInfo.InvariantCulture);

        // The message tells a missing reducer from one that kept everything.
        if (occurred != expectedOccurred)
            failures.Add($"summarizationOccurred: expected {expectedOccurred}, got {occurred}" + (match is null ? " (no reducer log line seen — is a reducer configured?)" : $" ({match.Groups["full"].Value} → {match.Groups["reduced"].Value} messages)"));
    }

    /// <summary>Cardinality and identity of the quick replies delivered with the message.</summary>
    private static void CheckQuickReplies(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // The buttons delivered with the message, empty when it carried none.
        IReadOnlyList<QuickReply> quickReplies = turn.QuickReplies;

        // The cardinality is written "none", "any", "count:N" or "min:N", split on the first colon into a verb and an operand.
        if (expect.QuickReplies is { Length: > 0 } cardinality)
        {
            string[] parts = cardinality.Split(':', 2);

            // A verb other than the four checks nothing.
            switch (parts[0].Trim().ToLowerInvariant())
            {
                // Buttons on a turn that must offer none.
                case "none" when quickReplies.Count > 0:
                    failures.Add($"quickReplies: expected none, got {quickReplies.Count} ({FormatIds(quickReplies)})");
                    break;

                // No button on a turn that must offer some.
                case "any" when quickReplies.Count == 0:
                    failures.Add("quickReplies: expected at least one, got none");
                    break;

                // A number of buttons other than the exact one required.
                case "count":
                    int expectedCount = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (quickReplies.Count != expectedCount)
                        failures.Add($"quickReplies: expected exactly {expectedCount}, got {quickReplies.Count} ({FormatIds(quickReplies)})");
                    break;

                // Fewer buttons than the minimum required.
                case "min":
                    int minimumCount = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (quickReplies.Count < minimumCount)
                        failures.Add($"quickReplies: expected at least {minimumCount}, got {quickReplies.Count} ({FormatIds(quickReplies)})");
                    break;
            }
        }

        // Each required id must be among the buttons, whatever the cardinality above asked.
        foreach (string id in expect.QuickReplyIds ?? [])
        {
            if (!quickReplies.Any(reply => string.Equals(reply.Id, id, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"quickReplyIds: '{id}' missing (got {FormatIds(quickReplies)})");
        }

        // Whole-list equality: the labels are what the user reads, so their order and count are part of the contract.
        if (expect.QuickReplyLabels is { } expectedLabels
            && !quickReplies.Select(reply => reply.Label).SequenceEqual(expectedLabels, StringComparer.Ordinal))
            failures.Add($"quickReplyLabels: expected [{string.Join(" | ", expectedLabels)}], got [{string.Join(" | ", quickReplies.Select(reply => reply.Label))}]");

        // Each forbidden id must be absent from the buttons.
        foreach (string id in expect.NoQuickReplyIds ?? [])
        {
            if (quickReplies.Any(reply => string.Equals(reply.Id, id, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"noQuickReplyIds: '{id}' present but must not be");
        }
    }

    /// <summary>Presence or absence of a rich card.</summary>
    private static void CheckRichCard(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // A turn declaring nothing about the card has nothing to check here.
        if (expect.RichCard is not { Length: > 0 } expectation)
            return;

        // Whether the delivered message carried a card at all.
        bool present = turn.Message.RichCard is not null;

        // A word other than the two checks nothing.
        switch (expectation.Trim().ToLowerInvariant())
        {
            // A card on a turn that must carry none, named by its title.
            case "absent" when present:
                failures.Add($"richCard: expected absent, got '{turn.Message.RichCard!.Title}'");
                break;

            // No card on a turn that must carry one.
            case "present" when !present:
                failures.Add("richCard: expected present, got none");
                break;
        }
    }

    /// <summary>Deterministic substrings that must appear in the rich card's text, as <see cref="RichCardText"/> renders it for the judge too.</summary>
    private static void CheckRichCardContains(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // A turn declaring no substring has nothing to check here.
        if (expect.RichCardContains is not { Count: > 0 } required)
            return;

        // The card's whole text; no card leaves every substring missing.
        string flattened = turn.Message.RichCard is null ? string.Empty : RichCardText.Flatten(turn.Message.RichCard);

        // Each substring is looked for on its own, so every missing one is reported.
        foreach (string substring in required)
        {
            if (!flattened.Contains(substring, StringComparison.OrdinalIgnoreCase))
                failures.Add($"richCardContains: '{substring}' not found in the rich card (got: {(turn.Message.RichCard is null ? "no card" : $"'{flattened}'")})");
        }
    }

    /// <summary>Which tools the turn invoked and in what order.</summary>
    private static void CheckTools(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // The tools the turn called in order, repetitions kept.
        IReadOnlyList<string> invoked = turn.ToolsInvoked;

        // Each required tool must have been called at least once.
        foreach (string tool in expect.ToolsCalled ?? [])
        {
            if (!invoked.Contains(tool, StringComparer.OrdinalIgnoreCase))
                failures.Add($"toolsCalled: '{tool}' was not invoked (got {FormatList(invoked)})");
        }

        // A second call to the same tool in one turn is an agent going round again instead of owning the gap.
        foreach (string tool in expect.ToolsCalledOnce ?? [])
        {
            int calls = invoked.Count(name => string.Equals(name, tool, StringComparison.OrdinalIgnoreCase));

            // Never called or called more than once both break it.
            if (calls != 1)
                failures.Add($"toolsCalledOnce: '{tool}' was invoked {calls} time(s) (got {FormatList(invoked)})");
        }

        // Each forbidden tool must never have been called.
        foreach (string tool in expect.ToolsNotCalled ?? [])
        {
            if (invoked.Contains(tool, StringComparer.OrdinalIgnoreCase))
                failures.Add($"toolsNotCalled: '{tool}' was invoked (got {FormatList(invoked)})");
        }

        // The turn must open with these tools in this order, whatever it calls after them.
        List<string> prefix = expect.ToolsCalledFirst ?? [];
        if (prefix.Count > 0)
        {
            // The calls must be at least as many as the prefix and agree with it position by position.
            bool matches = invoked.Count >= prefix.Count
                && prefix.Select((tool, index) => string.Equals(tool, invoked[index], StringComparison.OrdinalIgnoreCase)).All(match => match);

            // A turn that opened otherwise, such as one that answered before looking a value up.
            if (!matches)
                failures.Add($"toolsCalledFirst: expected the turn to open with {FormatList(prefix)}, got {FormatList(invoked)}");
        }
    }

    /// <summary>Context reads, writes and the closed vocabulary.</summary>
    private static void CheckContext(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // Each declared read must have happened, in the form its prefix names if it names one.
        foreach (string entry in expect.ContextReads ?? [])
        {
            (string? operation, string variable) = ParseContextReadEntry(entry);

            // A bare name matches a hit, a miss or a declaration; a prefixed one matches only that form.
            bool found = operation is null
                ? turn.ContextReads.Contains(variable, StringComparer.OrdinalIgnoreCase)
                : turn.ContextAccesses.Any(access =>
                    access.Operation != ContextOperation.Set
                    && string.Equals(access.Operation.ToString(), operation, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(access.VariableName, variable, StringComparison.OrdinalIgnoreCase));

            // The failure lists every read the turn did make.
            if (!found)
            {
                string got = string.Join(", ", turn.ContextAccesses
                    .Where(access => access.Operation != ContextOperation.Set)
                    .Select(access => $"{access.Operation}:{access.VariableName}"));
                failures.Add($"contextReads: '{entry}' was never read (got [{got}])");
            }
        }

        // Each declared write must have happened: a value the user gave and the agent did not write is lost to every later turn.
        foreach (string variable in expect.ContextWrites ?? [])
        {
            if (!turn.ContextWrites.Contains(variable, StringComparer.OrdinalIgnoreCase))
                failures.Add($"contextWrites: '{variable}' was never written (got {FormatList(turn.ContextWrites)})");
        }

        // A turn that must write nothing, such as one with no value to keep.
        if (expect.NoContextWrites is true && turn.ContextWrites.Count > 0)
            failures.Add($"noContextWrites: expected none, got {FormatList(turn.ContextWrites)}");

        // An agent declaring no context parameter has no legal name, so any access is an invention.
        if (expect.NoContextAccess is true && turn.ContextAccesses.Count > 0)
            failures.Add($"noContextAccess: expected the turn to touch no context variable, got {string.Join(", ", turn.ContextAccesses.Select(access => $"{access.Operation}:{access.VariableName}"))}");

        // The anti-invention check: every name the turn read or wrote must be in the declared vocabulary,
        // one failure per offending access so a turn inventing several names hides none of them.
        if (expect.ContextVocabulary is { Count: > 0 } vocabulary)
        {
            foreach (ContextAccess access in turn.ContextAccesses)
            {
                // A name outside the vocabulary was derived from somewhere the agent had no right to take a name from.
                if (!vocabulary.Contains(access.VariableName, StringComparer.OrdinalIgnoreCase))
                    failures.Add($"contextVocabulary: '{access.VariableName}' ({access.Operation}) is outside the declared vocabulary {FormatList(vocabulary)}");
            }
        }
    }

    /// <summary>Properties of the response text itself.</summary>
    private static void CheckText(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        // A turn always carries prose (MandatoryTextualResponse): buttons or a card alone are a fault.
        if (expect.TextNotEmpty is true && string.IsNullOrWhiteSpace(turn.Text))
            failures.Add("textNotEmpty: the response carried no text");

        // Each forbidden substring is looked for on its own, ignoring case.
        foreach (string forbidden in expect.TextNotContains ?? [])
        {
            if (turn.Text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                failures.Add($"textNotContains: response contains '{forbidden}'");
        }

        // A length-capped channel must have received text within its limit.
        if (expect.TextMaxLength is { } maxLength && turn.Text.Length > maxLength)
            failures.Add($"textMaxLength: expected at most {maxLength} characters, got {turn.Text.Length}");

        // A channel without markdown must have received plain words.
        if (expect.TextNotMarkdown is true && ContainsMarkdownSyntax(turn.Text))
            failures.Add($"textNotMarkdown: response still carries Markdown syntax: {turn.Text}");
    }

    /// <summary>
    /// Detects Markdown syntax in delivered text, derived here from the parsed document.
    /// The harness never calls <c>MorganaChannelAdapter</c>'s own detector: it judges the delivered text from outside the black box.
    /// </summary>
    private static bool ContainsMarkdownSyntax(string text)
    {
        // Every node of the parse is examined, inline ones included.
        MarkdownDocument document = Markdown.Parse(text);
        foreach (MarkdownObject node in document.Descendants())
        {
            // Lines of prose simply following one another are plain text: only a hard break
            // (trailing spaces, backslash) is markdown syntax
            if (node is not ParagraphBlock && node is not LiteralInline && node is not LineBreakInline { IsHard: false })
                return true;
        }

        // Only paragraphs of literals with soft breaks: plain text.
        return false;
    }

    /// <summary>Renders quick-reply ids for a failure message.</summary>
    private static string FormatIds(IReadOnlyList<QuickReply> quickReplies)
        => quickReplies.Count == 0 ? "none" : string.Join(", ", quickReplies.Select(reply => reply.Id));

    /// <summary>Renders a list for a failure message.</summary>
    private static string FormatList(IReadOnlyCollection<string> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);

    /// <summary>Splits a <c>contextReads</c> entry into its optional form prefix and variable name: <c>Hit:customerCode</c> gives <c>Hit</c> and <c>customerCode</c>.</summary>
    private static (string? Operation, string Variable) ParseContextReadEntry(string entry)
    {
        // A variable name never holds a colon, so the first one ends the prefix; none means a bare name.
        int colon = entry.IndexOf(':');
        return colon < 0 ? (null, entry) : (entry[..colon], entry[(colon + 1)..]);
    }
}