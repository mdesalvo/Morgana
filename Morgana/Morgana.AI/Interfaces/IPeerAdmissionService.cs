namespace Morgana.AI.Interfaces;

/// <summary>
/// How many conversations one admitted system may open on this installation within a sliding hour.
/// </summary>
/// <remarks>
/// The per-conversation budget says what one exchange may cost. Behind the A2A door that is only
/// half a bound: the caller names the conversation, so a system rotating names would draw a
/// fresh budget every time. What is missing is therefore not a second measure of spend but a bound
/// on how many exchanges may be started at all, from which the ceiling on spend follows as
/// admissions times the budget each one carries.
/// <para>The counted event is an exchange this installation has never seen. A system returning to
/// one it already opened is bounded by that conversation's own budget, so the two measures meet
/// without overlapping. This installation's own ring is exempt by construction — a colleague of
/// ours never opens a conversation, it joins the one the user is already having.</para>
/// </remarks>
public interface IPeerAdmissionService
{
    /// <summary>
    /// Weighs one system's request to open a conversation it has not opened before, recording it
    /// when it is admitted.
    /// </summary>
    /// <remarks>
    /// Fails open, as every limiter here does: a system is refused because it went too far, never
    /// because the count could not be read.
    /// </remarks>
    /// <param name="issuer">System asking, as its token declared it.</param>
    /// <returns><c>true</c> when the conversation may be opened.</returns>
    Task<bool> TryAdmitNewConversationAsync(string issuer);
}