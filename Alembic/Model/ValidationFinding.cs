namespace Alembic.Model;

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
/// Every finding here is decidable by reading the Draft — no model is asked, and none could help.
/// That is the whole point of running this pass before the recap: the client should never be shown
/// a beautifully composed prompt for a domain that would not start.
/// <para>
/// Most of these restate a check the framework already performs at startup. Duplicating them is
/// deliberate and is the entire value: a startup exception arrives after the client has packaged,
/// deployed and run, whereas the same sentence here arrives while they are still authoring.
/// </para>
/// </remarks>
/// <param name="Severity">Whether this stops the domain or merely deserves a look.</param>
/// <param name="Where">What the finding is about, e.g. <c>billing.GetInvoices.userId</c>.</param>
/// <param name="Message">What is wrong, in the terms the client authored it in.</param>
/// <param name="Because">
/// Which rule of Morgana's makes it so — named, so the finding teaches rather than just refuses.
/// </param>
public sealed record ValidationFinding(
    FindingSeverity Severity,
    string Where,
    string Message,
    string Because);
