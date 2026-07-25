using System.Globalization;
using Morgana.Contracts;
using Morgana.Tests.NRT.Infrastructure;

namespace Morgana.Tests.NRT.Scenarios;

/// <summary>
/// Evaluates a turn's structural expectations. Deterministic by construction: everything it reads
/// is either on the delivered <see cref="ChannelMessage"/> or on the span and log signals the
/// <see cref="TurnObserver"/> collected — never on the wording of the response.
/// </summary>
public static class ExpectationChecker
{
    /// <summary>The two ids the framework reserves for escaping a flow, declared once in <c>morgana.json</c>.</summary>
    private static readonly HashSet<string> EscapeOptionIds =
        new HashSet<string>(["continue_agent", "exit_agent"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns one message per violated expectation; empty when the turn conforms.</summary>
    public static IReadOnlyList<string> Check(ExpectSpec expect, TurnResult turn)
    {
        List<string> failures = [];

        if (expect.AgentCompleted is bool expectedCompleted && turn.Message.AgentCompleted != expectedCompleted)
            failures.Add($"agentCompleted: expected {expectedCompleted}, got {turn.Message.AgentCompleted}");

        if (expect.Agent is { Length: > 0 } expectedAgent
            && !string.Equals(turn.AgentName, expectedAgent, StringComparison.OrdinalIgnoreCase))
            failures.Add($"agent: expected {expectedAgent}, got {turn.AgentName ?? "(none)"}");

        CheckQuickReplies(expect, turn, failures);
        CheckRichCard(expect, turn, failures);
        CheckTools(expect, turn, failures);
        CheckContext(expect, turn, failures);
        CheckText(expect, turn, failures);

        return failures;
    }

    /// <summary>Cardinality and identity of the quick replies delivered with the message.</summary>
    private static void CheckQuickReplies(ExpectSpec expect, TurnResult turn, List<string> failures)
    {
        IReadOnlyList<QuickReply> quickReplies = turn.QuickReplies;

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

        if (expect.NoStandaloneEscapeOptions is true
            && quickReplies.Count > 0
            && quickReplies.All(reply => EscapeOptionIds.Contains(reply.Id)))
            failures.Add(
                $"noStandaloneEscapeOptions: the turn emitted only the escape pair ({FormatIds(quickReplies)}) "
              + "with no primary option beside it");
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
        foreach (string variable in expect.ContextReads ?? [])
        {
            if (!turn.ContextReads.Contains(variable, StringComparer.OrdinalIgnoreCase))
                failures.Add($"contextReads: '{variable}' was never read (got {FormatList(turn.ContextReads)})");
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
    }

    /// <summary>Renders quick-reply ids for a failure message.</summary>
    private static string FormatIds(IReadOnlyList<QuickReply> quickReplies)
        => quickReplies.Count == 0 ? "none" : string.Join(", ", quickReplies.Select(reply => reply.Id));

    /// <summary>Renders a list for a failure message.</summary>
    private static string FormatList(IReadOnlyCollection<string> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);
}
