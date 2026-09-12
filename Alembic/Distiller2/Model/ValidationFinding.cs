namespace Distiller2.Model;

/// <summary>
/// How badly a finding matters.
/// </summary>
public enum FindingSeverity
{
    /// <summary>
    /// The domain is legal but something about it is worth the client's attention.
    /// </summary>
    Warning,

    /// <summary>
    /// Morgana would refuse this domain, or the C# it implies could not be written.
    /// </summary>
    Error
}

/// <summary>
/// One deterministic observation about a Draft.
/// </summary>
/// <remarks>
/// Every finding here is decidable by reading the Draft — no model is asked and none could help.
/// That is the whole point of running this pass before the recap: the client should never be shown
/// a beautifully composed prompt for a domain that would not start.
/// <para>
/// Most of these restate a check the framework already performs at startup. Duplicating them is
/// deliberate and is the entire value: a startup exception arrives after the client has packaged,
/// deployed and run, whereas the same sentence here arrives while they are still authoring.
/// </para>
/// </remarks>
/// <param name="Severity">Whether this stops the domain or merely deserves a look.</param>
/// <param name="Where">What the finding is about, e.g. <c>billing.GetInvoices.customerCode</c>.</param>
/// <param name="Message">What is wrong, in the terms the client authored it in.</param>
/// <param name="Because">
/// Which rule of Morgana's makes it so — named, so the finding teaches rather than just refuses.
/// </param>
public sealed record ValidationFinding(
    FindingSeverity Severity,
    string Where,
    string Message,
    string Because)
{
    /// <summary>
    /// The check that raised it, so findings are read grouped under the rule family they belong to.
    /// </summary>
    public ValidationCheck Check { get; init; }

    /// <summary>
    /// The agent the finding is about, by the intent name that reaches it; <c>null</c> when none is.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// The interview step that writes what is wrong, when the rewrite is a section of the agent.
    /// </summary>
    public InterviewStep? Step { get; init; }
}

/// <summary>
/// The families of deterministic check, one per rule of Morgana's the validator restates.
/// </summary>
public enum ValidationCheck
{
    /// <summary>Intents are named, unique and described.</summary>
    Intents,

    /// <summary>Every intent has its agent and every agent its intent.</summary>
    Routing,

    /// <summary>Every declared colleague can be reached.</summary>
    Colleagues,

    /// <summary>An agent's own sections are written and publishable.</summary>
    Agents,

    /// <summary>Tools and parameters can become the C# they are emitted as.</summary>
    Tools,

    /// <summary>Class names can be C# identifiers.</summary>
    Code
}
