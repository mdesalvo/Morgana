namespace Distiller.Model;

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
    /// <param name="What">What it is, as a noun — the rail's own for the four sections it names.</param>
    /// <param name="Value">What has been written, or <c>null</c> while it is still unasked.</param>
    /// <param name="Moved">Whether the last exchange is what put it there.</param>
    public sealed record Row(string What, string? Value, bool Moved);

    /// <summary>
    /// Everything this agent is made of.
    /// </summary>
    public static IReadOnlyList<Row> Of(InterviewState interviewState)
    {
        // Nouns, and for the four sections the rail's own nouns. "It is theirs when", "how it meets
        // them", "what it reaches for" were sentences with pronouns in them, and a column of those is
        // read as prose rather than scanned as labels — the client has to work out what "them" is
        // before they can find the line they are looking for. Naming the sections exactly as the step
        // that settles them is named makes the panel and the rail the same vocabulary: what you were
        // just asked for is the row that just filled in.
        List<Row> rows =
        [
            new Row("name", interviewState.Intent.Name, interviewState.Changed.Contains("intentName")),
            new Row("description", interviewState.Intent.Description, interviewState.Changed.Contains("intentDescription")),
            new Row("button", Button(interviewState.Intent),
                interviewState.Changed.Contains("intentLabel") || interviewState.Changed.Contains("intentDefaultValue")),
            new Row("Target", Plain(interviewState.Agent.Target), interviewState.Changed.Contains("agentTarget")),
            new Row("Consult me for", Plain(interviewState.Agent.ConsultMeFor), interviewState.Changed.Contains("agentConsultMeFor")),
            new Row("Personality", Plain(interviewState.Agent.Personality), interviewState.Changed.Contains("agentPersonality"))
        ];

        rows.Add(new Row("Toolkit", Toolkit(interviewState.Agent.Tools), interviewState.Changed.Contains("tools")));

        rows.Add(new Row("Instructions", Plain(interviewState.Agent.Instructions), interviewState.Changed.Contains("agentInstructions")));
        rows.Add(new Row("Formatting", Plain(interviewState.Agent.Formatting), interviewState.Changed.Contains("agentFormatting")));

        return rows;
    }

    /// <summary>
    /// A quick reply as the user will meet it: what is written on it, and what pressing it sends.
    /// </summary>
    // One row, because it is one thing. A button whose label is read apart from the sentence it sends
    // is half of a button, and the half that is missing is the one that decides whether pressing it
    // does what it looked like it would.
    private static string? Button(IntentDraft intent) =>
        intent.Label is null && intent.DefaultValue is null
            ? null
            : $"{intent.Label ?? "…"}  |  {intent.DefaultValue ?? "…"}";

    /// <summary>
    /// The toolkit's own <see cref="Row.Value"/>: not what actually reaches the client, only enough
    /// to say whether the row is empty and to give <see cref="Written"/> something to count. The view
    /// itself renders the tools it stands for straight from <see cref="ToolDraft"/>, one line each
    /// with its signature bolded — real markup, so this stays plain text.
    /// </summary>
    public static string? Toolkit(IReadOnlyList<ToolDraft> tools) =>
        tools.Count == 0 ? null : string.Join(" · ", tools.Select(Signature));

    /// <summary>
    /// A tool's name and parameters, plain text, exactly as the client named both.
    /// </summary>
    public static string Signature(ToolDraft tool) =>
        (tool.Name ?? "(unnamed)") + (tool.Parameters.Count == 0
            ? string.Empty
            : $"({string.Join(", ", tool.Parameters.Select(p => p.Name ?? "…"))})");

    /// <summary>
    /// How many rows have something in them.
    /// </summary>
    public static int Written(IReadOnlyList<Row> rows) => rows.Count(row => row.Value is not null);

    /// <summary>
    /// A written section as the client should read it: without the label the framework fences it in.
    /// </summary>
    /// <remarks>
    /// Public because two screens read prose the interview wrote and neither should meet the fence:
    /// the panel behind the entry's tag, and the walk over a domain's agents.
    /// </remarks>
    // [TARGET] is a marker for the model that reads the composed prompt, guaranteed in code precisely
    // because it must not depend on anyone remembering it. It is not a word of this client's, and a
    // panel written for them is the wrong place to meet it.
    public static string? Plain(string? section) =>
        section is null ? null : Labelled.Replace(section, string.Empty).TrimStart();

    private static readonly System.Text.RegularExpressions.Regex Labelled =
        new(@"^\s*\[[A-Z]+\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);
}
