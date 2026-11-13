namespace Alembic.Models;

/// <summary>
/// What has been written down about the agent in hand, in the order it gets written.
/// The entry's tag counts the rows and the panel under it shows them, so both read one projection and can never disagree.
/// </summary>
public static class AgentRows
{
    /// <summary>One row of the panel.</summary>
    /// <param name="What">What it is, as a noun — the rail's own for the four sections it names.</param>
    /// <param name="Value">What has been written; <c>null</c> while it is still unasked.</param>
    /// <param name="Moved">Whether the last exchange is what put it there.</param>
    public sealed record Row(string What, string? Value, bool Moved);

    /// <summary>Everything this agent is made of, one row per field the interview writes.</summary>
    public static IReadOnlyList<Row> Of(InterviewState interviewState)
    {
        // Nouns and for the four sections the rail's own nouns. "It is theirs when", "how it meets
        // them", "what it reaches for" were sentences with pronouns in them and a column of those is
        // read as prose rather than scanned as labels — the client has to work out what "them" is
        // before they can find the line they are looking for. Naming the sections exactly as the step
        // that settles them is named makes the panel and the rail the same vocabulary: what you were
        // just asked for is the row that just filled in.
        List<Row> rows =
        [
            new Row("name", interviewState.Intent.Name, interviewState.Changed.Contains("intentName")),
            new Row("description", interviewState.Intent.Description,
                interviewState.Changed.Contains("intentDescription")),
            new Row("button", Button(interviewState.Intent),
                interviewState.Changed.Contains("intentLabel") ||
                interviewState.Changed.Contains("intentDefaultValue")),
            new Row("Target", Plain(interviewState.Agent.Target), interviewState.Changed.Contains("agentTarget")),
            new Row("Personality", Plain(interviewState.Agent.Personality),
                interviewState.Changed.Contains("agentPersonality")),
            new Row("Toolkit", Toolkit(interviewState.Agent.Tools), interviewState.Changed.Contains("tools")),
            new Row("Territory", Plain(interviewState.Agent.Territory),
                interviewState.Changed.Contains("agentTerritory")),
            new Row("Instructions", Plain(interviewState.Agent.Instructions),
                interviewState.Changed.Contains("agentInstructions")),
            new Row("Formatting", Plain(interviewState.Agent.Formatting),
                interviewState.Changed.Contains("agentFormatting"))
        ];

        // In the order the interview writes them, which is the order the client watches them fill.
        return rows;
    }

    /// <summary>
    /// A quick reply as the user will meet it, what is written on it and what pressing it sends, as one row.
    /// A label read apart from its sentence is half a button, missing the half that decides whether pressing it does what it looked like.
    /// </summary>
    private static string? Button(IntentDraft intent) =>
        intent.Label is null && intent.DefaultValue is null
            ? null
            : $"{intent.Label ?? "…"}  |  {intent.DefaultValue ?? "…"}";

    /// <summary>
    /// The toolkit's own <see cref="Row.Value"/>, enough to say whether the row is empty and to give <see cref="Written"/> something to count.
    /// The panel renders the tools themselves from <see cref="ToolDraft"/>, so this stays plain text.
    /// </summary>
    public static string? Toolkit(IReadOnlyList<ToolDraft> tools) =>
        tools.Count == 0 ? null : string.Join(" · ", tools.Select(Signature));

    /// <summary>A tool's name and parameters, plain text, exactly as the client named both.</summary>
    public static string Signature(ToolDraft tool) =>
        (tool.Name ?? "(unnamed)") + (tool.Parameters.Count == 0
            ? string.Empty
            : $"({string.Join(", ", tool.Parameters.Select(p => p.Name ?? "…"))})");

    /// <summary>How many rows have something in them, the count the entry's tag shows.</summary>
    public static int Written(IReadOnlyList<Row> rows) => rows.Count(row => row.Value is not null);

    /// <summary>
    /// A written section as the client should read it, without a label opening it.
    /// A label such as [TARGET] is a marker for the model reading the composed prompt and no word of this client's.
    /// </summary>
    public static string? Plain(string? section) =>
        section is null ? null : Labelled.Replace(section, string.Empty).TrimStart();

    /// <summary>A bracketed all-caps label at the head of a section, as a configuration written before the composer labelled sections may carry it.</summary>
    private static readonly System.Text.RegularExpressions.Regex Labelled =
        new(@"^\s*\[[A-Z ]+\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);
}