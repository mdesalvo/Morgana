namespace Alembic.Model;

/// <summary>
/// What has been written down about the agent in hand, in the order it gets written.
/// </summary>
/// <remarks>
/// A projection of the interview rather than a component's private business, because two things on
/// the screen need it and they are not the same component: the entry's own tag says how much of it
/// is written, and the panel that opens under that tag says what. Computed in one place so the count
/// on the tag can never disagree with the rows behind it.
/// </remarks>
public static class AgentRows
{
    /// <summary>
    /// One row.
    /// </summary>
    /// <param name="What">The client's name for it. Never a field name.</param>
    /// <param name="Value">What has been written, or <c>null</c> while it is still unasked.</param>
    /// <param name="Moved">Whether the last exchange is what put it there.</param>
    public sealed record Row(string What, string? Value, bool Moved);

    /// <summary>
    /// Everything this agent is made of.
    /// </summary>
    public static IReadOnlyList<Row> Of(InterviewState interviewState)
    {
        List<Row> rows =
        [
            new Row("it is called", interviewState.Intent.Name, interviewState.Changed.Contains("intentName")),
            new Row("it is theirs when", interviewState.Intent.Description, interviewState.Changed.Contains("intentDescription")),
            new Row("its button", interviewState.Intent.Label, interviewState.Changed.Contains("intentLabel")),
            new Row("it opens with", interviewState.Intent.DefaultValue, interviewState.Changed.Contains("intentDefaultValue")),
            new Row("what it is for", Plain(interviewState.Agent.Target), interviewState.Changed.Contains("agentTarget")),
            new Row("how it meets them", Plain(interviewState.Agent.Personality), interviewState.Changed.Contains("agentPersonality"))
        ];

        bool toolsMoved = interviewState.Changed.Contains("tools");

        if (interviewState.Agent.Tools.Count == 0)
            rows.Add(new Row("what it reaches for", null, toolsMoved));
        else
            rows.AddRange(interviewState.Agent.Tools.Select(t => new Row("it can", t.Name, toolsMoved)));

        rows.Add(new Row("the order of the work", Plain(interviewState.Agent.Instructions), interviewState.Changed.Contains("agentInstructions")));
        rows.Add(new Row("how it shows what it finds", Plain(interviewState.Agent.Formatting), interviewState.Changed.Contains("agentFormatting")));

        return rows;
    }

    /// <summary>
    /// How many rows have something in them.
    /// </summary>
    public static int Written(IReadOnlyList<Row> rows) => rows.Count(row => row.Value is not null);

    /// <summary>
    /// A written section as the client should read it: without the label the framework fences it in.
    /// </summary>
    // [TARGET] is a marker for the model that reads the composed prompt, guaranteed in code precisely
    // because it must not depend on anyone remembering it. It is not a word of this client's, and a
    // panel written for them is the wrong place to meet it.
    private static string? Plain(string? section) =>
        section is null ? null : Labelled.Replace(section, string.Empty).TrimStart();

    private static readonly System.Text.RegularExpressions.Regex Labelled =
        new(@"^\s*\[[A-Z]+\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);
}
