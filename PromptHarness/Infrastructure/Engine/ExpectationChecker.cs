using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// Evaluates a turn's structural expectations. Deterministic by construction: everything it reads
/// is either on the delivered <see cref="ChannelMessage"/> or on the span and log signals the
/// <see cref="TurnObserver"/> collected — never on the wording of the response.
/// </summary>
public static partial class ExpectationChecker
{
    /// <summary>The two ids the framework reserves for escaping a flow, declared once in <c>morgana.json</c>.</summary>
    private static readonly HashSet<string> EscapeOptionIds =
        new HashSet<string>(["continue_agent", "exit_agent"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Matches <c>MorganaChatHistoryProvider</c>'s per-turn log line, which fires every turn a
    /// reducer is configured regardless of whether it actually shrank anything this time — the two
    /// counts are what distinguish "reducer present" from "reduction happened".
    /// </summary>
    [GeneratedRegex(@"PROVIDING reduced view \((?<full>\d+) → (?<reduced>\d+) messages\)", RegexOptions.CultureInvariant)]
    private static partial Regex SummarizationLogPattern { get; }

    /// <summary>Returns one message per violated expectation; empty when the turn conforms.</summary>
    public static IReadOnlyList<string> Check(ExpectSpec expect, TurnResult turn)
    {
        List<string> failures = [];

        // Every check below follows the same shape: a property being null/unset means "the
        // scenario doesn't care about this", so nothing is added to failures; only an explicitly
        // stated expectation that does not match observed reality produces a message.
        if (expect.AgentCompleted is { } expectedCompleted && turn.Message.AgentCompleted != expectedCompleted)
            failures.Add($"agentCompleted: expected {expectedCompleted}, got {turn.Message.AgentCompleted}");

        if (expect.Agent is { Length: > 0 } expectedAgent
            && !string.Equals(turn.AgentName, expectedAgent, StringComparison.OrdinalIgnoreCase))
            failures.Add($"agent: expected {expectedAgent}, got {turn.AgentName ?? "(none)"}");

        // Delegated to specialised methods below, one per concern, so a scenario touching several
        // of these still produces every relevant failure in one pass rather than stopping at the first.
        CheckQuickReplies(expect, turn, failures);
        CheckRichCard(expect, turn, failures);
        CheckTools(expect, turn, failures);
        CheckContext(expect, turn, failures);
        CheckText(expect, turn, failures);
        CheckGuard(expect, turn, failures);
        CheckClassifier(expect, turn, failures);
        CheckSummarization(expect, turn, failures);

        return failures;
    }

    /// <summary>Verdict of the <c>morgana.guard</c> span.</summary>
    private static void CheckGuard(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        if (expect.GuardCompliant is { } expectedCompliant && turn.GuardCompliant != expectedCompliant)
            failures.Add($"guardCompliant: expected {expectedCompliant}, got {turn.GuardCompliant?.ToString() ?? "(no guard span — is Harness:EnableGuardrail on?)"}");
    }

    /// <summary>Intent and confidence of the <c>morgana.classifier</c> span.</summary>
    private static void CheckClassifier(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        if (expect.ClassifierIntent is { Length: > 0 } expectedIntent
            && !string.Equals(turn.ClassifierIntent, expectedIntent, StringComparison.OrdinalIgnoreCase))
            failures.Add($"classifierIntent: expected {expectedIntent}, got {turn.ClassifierIntent ?? "(none — was this a follow-up turn?)"}");

        if (expect.ClassifierMinConfidence is { } minimumConfidence
            && (turn.ClassifierConfidence is not { } actualConfidence || actualConfidence < minimumConfidence))
            failures.Add($"classifierMinConfidence: expected at least {minimumConfidence:F2}, got {turn.ClassifierConfidence?.ToString("F2") ?? "(none)"}");
    }

    /// <summary>
    /// Whether <c>MorganaChatHistoryProvider</c>'s reducer actually shrank the conversation before
    /// this turn — the log line fires every turn a reducer is configured, so "occurred" means the
    /// two counts in the last such line for this turn differ, not merely that the line exists.
    /// </summary>
    private static void CheckSummarization(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        if (expect.SummarizationOccurred is not { } expectedOccurred)
            return;

        // Take the last match, not the first: a turn can carry the line more than once if the
        // agent's own tool-loop causes ProvideChatHistoryAsync to be called via a nested path — the
        // most recent read is the one that decided what the LLM actually saw this turn.
        Match? match = null;
        foreach (string line in turn.LogLines)
        {
            Match candidate = SummarizationLogPattern.Match(line);
            if (candidate.Success)
                match = candidate;
        }

        bool occurred = match is not null
            && int.Parse(match.Groups["full"].Value, CultureInfo.InvariantCulture) > int.Parse(match.Groups["reduced"].Value, CultureInfo.InvariantCulture);

        if (occurred != expectedOccurred)
            failures.Add($"summarizationOccurred: expected {expectedOccurred}, got {occurred}" + (match is null ? " (no reducer log line seen — is a reducer configured?)" : $" ({match.Groups["full"].Value} → {match.Groups["reduced"].Value} messages)"));
    }

    /// <summary>Cardinality and identity of the quick replies delivered with the message.</summary>
    private static void CheckQuickReplies(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        IReadOnlyList<QuickReply> quickReplies = turn.QuickReplies;

        // Cardinality is a small mini-language in the YAML ("none", "any", "count:N", "min:N"),
        // split on the first colon so "count:3" separates into a verb and an operand.
        if (expect.QuickReplies is { Length: > 0 } cardinality)
        {
            string[] parts = cardinality.Split(':', 2);
            switch (parts[0].Trim().ToLowerInvariant())
            {
                case "none" when quickReplies.Count > 0:
                    failures.Add($"quickReplies: expected none, got {quickReplies.Count} ({FormatIds(quickReplies)})");
                    break;

                case "any" when quickReplies.Count == 0:
                    failures.Add("quickReplies: expected at least one, got none");
                    break;

                case "count":
                    int expectedCount = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (quickReplies.Count != expectedCount)
                        failures.Add($"quickReplies: expected exactly {expectedCount}, got {quickReplies.Count} ({FormatIds(quickReplies)})");
                    break;

                case "min":
                    int minimumCount = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (quickReplies.Count < minimumCount)
                        failures.Add($"quickReplies: expected at least {minimumCount}, got {quickReplies.Count} ({FormatIds(quickReplies)})");
                    break;
            }
        }

        // Presence/absence of specific ids — independent of the cardinality check above, so a
        // scenario can combine e.g. "min:2" with "must include this specific id" in one turn.
        foreach (string id in expect.QuickReplyIds ?? [])
        {
            if (!quickReplies.Any(reply => string.Equals(reply.Id, id, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"quickReplyIds: '{id}' missing (got {FormatIds(quickReplies)})");
        }

        foreach (string id in expect.NoQuickReplyIds ?? [])
        {
            if (quickReplies.Any(reply => string.Equals(reply.Id, id, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"noQuickReplyIds: '{id}' present but must not be");
        }

        // The two escape-pair rules are complementary halves of one policy (QuickReplyEscapeOptions
        // in the framework's own prose): this one fails when the escape pair is emitted alone, with
        // no primary option beside it — "must not stand alone" — see the property's own <remarks>
        // for why it's opt-in rather than always checked.
        if (expect.NoStandaloneEscapeOptions is true
            && quickReplies.Count > 0
            && quickReplies.All(reply => EscapeOptionIds.Contains(reply.Id)))
            failures.Add(
                $"noStandaloneEscapeOptions: the turn emitted only the escape pair ({FormatIds(quickReplies)}) "
              + "with no primary option beside it");

        // The other half: whenever there's at least one non-escape (primary) option, both escape
        // ids must also be present — "must always be appended to a choice list" — checked by
        // negating EscapeOptionIds.All, i.e. failing when the pair is *not* complete.
        if (expect.EscapeOptionsWithPrimary is true
            && quickReplies.Any(reply => !EscapeOptionIds.Contains(reply.Id))
            && !EscapeOptionIds.All(id => quickReplies.Any(reply => string.Equals(reply.Id, id, StringComparison.OrdinalIgnoreCase))))
            failures.Add(
                $"escapeOptionsWithPrimary: the turn offered primary options ({FormatIds(quickReplies)}) "
              + "without appending the escape pair, trapping the user in the list");
    }

    /// <summary>Presence or absence of a rich card.</summary>
    private static void CheckRichCard(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        if (expect.RichCard is not { Length: > 0 } expectation)
            return;

        bool present = turn.Message.RichCard is not null;
        switch (expectation.Trim().ToLowerInvariant())
        {
            case "absent" when present:
                failures.Add($"richCard: expected absent, got '{turn.Message.RichCard!.Title}'");
                break;

            case "present" when !present:
                failures.Add("richCard: expected present, got none");
                break;
        }
    }

    /// <summary>Which tools the turn invoked, and in what order.</summary>
    private static void CheckTools(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        IReadOnlyList<string> invoked = turn.ToolsInvoked;

        foreach (string tool in expect.ToolsCalled ?? [])
        {
            if (!invoked.Contains(tool, StringComparer.OrdinalIgnoreCase))
                failures.Add($"toolsCalled: '{tool}' was not invoked (got {FormatList(invoked)})");
        }

        foreach (string tool in expect.ToolsNotCalled ?? [])
        {
            if (invoked.Contains(tool, StringComparer.OrdinalIgnoreCase))
                failures.Add($"toolsNotCalled: '{tool}' was invoked (got {FormatList(invoked)})");
        }

        // A prefix check, not an exact-match one: the turn may invoke more tools after the
        // required opening sequence, so invoked only has to be at least as long as prefix and
        // agree position-by-position for that leading portion.
        List<string> prefix = expect.ToolsCalledFirst ?? [];
        if (prefix.Count > 0)
        {
            bool matches = invoked.Count >= prefix.Count
                && prefix.Select((tool, index) => string.Equals(tool, invoked[index], StringComparison.OrdinalIgnoreCase)).All(match => match);

            if (!matches)
                failures.Add($"toolsCalledFirst: expected the turn to open with {FormatList(prefix)}, got {FormatList(invoked)}");
        }
    }

    /// <summary>Context reads, writes and the closed vocabulary.</summary>
    private static void CheckContext(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        foreach (string entry in expect.ContextReads ?? [])
        {
            (string? operation, string variable) = ParseContextReadEntry(entry);

            bool found = operation is null
                ? turn.ContextReads.Contains(variable, StringComparer.OrdinalIgnoreCase)
                : turn.ContextAccesses.Any(access =>
                    access.Operation != ContextOperation.Set
                    && string.Equals(access.Operation.ToString(), operation, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(access.VariableName, variable, StringComparison.OrdinalIgnoreCase));

            if (!found)
            {
                string got = string.Join(", ", turn.ContextAccesses
                    .Where(access => access.Operation != ContextOperation.Set)
                    .Select(access => $"{access.Operation}:{access.VariableName}"));
                failures.Add($"contextReads: '{entry}' was never read (got [{got}])");
            }
        }

        foreach (string variable in expect.ContextWrites ?? [])
        {
            if (!turn.ContextWrites.Contains(variable, StringComparer.OrdinalIgnoreCase))
                failures.Add($"contextWrites: '{variable}' was never written (got {FormatList(turn.ContextWrites)})");
        }

        if (expect.NoContextWrites is true && turn.ContextWrites.Count > 0)
            failures.Add($"noContextWrites: expected none, got {FormatList(turn.ContextWrites)}");

        if (expect.NoContextAccess is true && turn.ContextAccesses.Count > 0)
            failures.Add($"noContextAccess: expected the turn to touch no context variable, got {string.Join(", ", turn.ContextAccesses.Select(access => $"{access.Operation}:{access.VariableName}"))}");

        // The anti-invention check: every name the turn actually touched (read or write) must
        // appear in the scenario's declared vocabulary. One failure per offending access, not just
        // the first, so a turn that invents several names in one go doesn't hide the rest.
        if (expect.ContextVocabulary is { Count: > 0 } vocabulary)
        {
            foreach (ContextAccess access in turn.ContextAccesses)
            {
                if (!vocabulary.Contains(access.VariableName, StringComparer.OrdinalIgnoreCase))
                    failures.Add($"contextVocabulary: '{access.VariableName}' ({access.Operation}) is outside the declared vocabulary {FormatList(vocabulary)}");
            }
        }
    }

    /// <summary>Properties of the response text itself.</summary>
    private static void CheckText(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        if (expect.TextNotEmpty is true && string.IsNullOrWhiteSpace(turn.Text))
            failures.Add("textNotEmpty: the response carried no text");

        foreach (string forbidden in expect.TextNotContains ?? [])
        {
            if (turn.Text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                failures.Add($"textNotContains: response contains '{forbidden}'");
        }

        if (expect.TextMaxLength is { } maxLength && turn.Text.Length > maxLength)
            failures.Add($"textMaxLength: expected at most {maxLength} characters, got {turn.Text.Length}");

        if (expect.TextNotMarkdown is true && ContainsMarkdownSyntax(turn.Text))
            failures.Add($"textNotMarkdown: response still carries Markdown syntax: {turn.Text}");
    }

    /// <summary>
    /// Detects Markdown syntax in delivered text, re-derived locally from the parsed document
    /// rather than by calling into <c>MorganaChannelAdapter</c>'s own private detector — the harness
    /// judges the delivered text on its own terms, keeping the black-box boundary structural.
    /// </summary>
    private static bool ContainsMarkdownSyntax(string text)
    {
        MarkdownDocument document = Markdown.Parse(text);
        foreach (MarkdownObject node in document.Descendants())
        {
            if (node is not ParagraphBlock && node is not LiteralInline)
                return true;
        }

        return false;
    }

    /// <summary>Renders quick-reply ids for a failure message.</summary>
    private static string FormatIds(IReadOnlyList<QuickReply> quickReplies)
        => quickReplies.Count == 0 ? "none" : string.Join(", ", quickReplies.Select(reply => reply.Id));

    /// <summary>Renders a list for a failure message.</summary>
    private static string FormatList(IReadOnlyCollection<string> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);

    /// <summary>
    /// Splits a <c>contextReads</c> entry into its optional outcome prefix and variable name —
    /// <c>"Hit:userId"</c> → <c>("Hit", "userId")</c>, <c>"userId"</c> → <c>(null, "userId")</c>.
    /// </summary>
    private static (string? Operation, string Variable) ParseContextReadEntry(string entry)
    {
        int colon = entry.IndexOf(':');
        return colon < 0 ? (null, entry) : (entry[..colon], entry[(colon + 1)..]);
    }
}
