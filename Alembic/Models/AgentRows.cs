namespace Alembic.Models;

/// <summary>
/// What has been written down about the agent in hand, in the order it gets written.
/// </summary>
/// <remarks>The entry's tag and the panel under it both read these rows, so the count on the tag always agrees with them.</remarks>
public static class AgentRows
{
    /// <summary>
    /// One thing written about the agent, labelled with the noun the rail uses.
    /// </summary>
    /// <param name="What">The label the client scans, named as the step that settles it.</param>
    /// <param name="Value">What has been written, or <c>null</c> while it is still unasked.</param>
    /// <param name="Moved">Whether the last exchange is what wrote it.</param>
    public sealed record Row(string What, string? Value, bool Moved);

    /// <summary>
    /// Every row of the agent in hand, in the order the interview writes them.
    /// </summary>
    public static IReadOnlyList<Row> Of(InterviewState interviewState)
    {
        // Rows are labelled with the rail's own nouns, so the row that just filled in is the one the client was just asked for.
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
            new Row("Workflows", Workflows(interviewState.Agent.Workflows), interviewState.Changed.Contains("workflows")),
            new Row("Territory", Plain(interviewState.Agent.Territory),
                interviewState.Changed.Contains("agentTerritory")),
            new Row("Instructions", Plain(interviewState.Agent.Instructions),
                interviewState.Changed.Contains("agentInstructions")),
            new Row("Formatting", Plain(interviewState.Agent.Formatting),
                interviewState.Changed.Contains("agentFormatting"))
        ];

        // The rows go to the tag and to the panel behind it.
        return rows;
    }

    /// <summary>
    /// A quick reply as the user will meet it: what is written on it and what pressing it sends.
    /// </summary>
    private static string? Button(IntentDraft intent) =>
        // Label and sentence share one row, since a label read apart from what it sends is half a button.
        intent.Label is null && intent.DefaultValue is null
            ? null
            : $"{intent.Label ?? "…"}  |  {intent.DefaultValue ?? "…"}";

    /// <summary>
    /// The toolkit row's value: plain signatures that tell whether the row is empty while the view renders the tools itself.
    /// </summary>
    public static string? Toolkit(IReadOnlyList<ToolDraft> tools) =>
        // No tool leaves the row unasked.
        tools.Count == 0 ? null : string.Join(" · ", tools.Select(Signature));

    /// <summary>
    /// The workflows row's value: the workflow names that tell whether the row is empty while the view renders the steps itself.
    /// </summary>
    public static string? Workflows(IReadOnlyList<WorkflowDraft> workflows) =>
        // No workflow leaves the row unasked.
        workflows.Count == 0 ? null : string.Join(" · ", workflows.Select(workflow => workflow.Name ?? "(unnamed)"));

    /// <summary>
    /// A tool's name and parameters, plain text, exactly as the client named both.
    /// </summary>
    public static string Signature(ToolDraft tool) =>
        // A name still unasked shows as a placeholder so the signature stays readable mid-interview.
        (tool.Name ?? "(unnamed)") + (tool.Parameters.Count == 0
            ? string.Empty
            : $"({string.Join(", ", tool.Parameters.Select(p => p.Name ?? "…"))})");

    /// <summary>
    /// How many rows have something written, the count the entry's tag shows.
    /// </summary>
    public static int Written(IReadOnlyList<Row> rows) => rows.Count(row => row.Value is not null);

    /// <summary>
    /// A written section as the client should read it: without the label the framework fences it in.
    /// </summary>
    /// <remarks>The entry's panel and the walk over a domain's agents both show prose without the fence.</remarks>
    public static string? Plain(string? section) =>
        // The leading [TARGET]-style marker is for the model alone and is not a word of the client's.
        section is null ? null : Labelled.Replace(section, string.Empty).TrimStart();

    /// <summary>Matches the bracketed label that the framework fences a section in.</summary>
    private static readonly System.Text.RegularExpressions.Regex Labelled =
        new(@"^\s*\[[A-Z ]+\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);
}
