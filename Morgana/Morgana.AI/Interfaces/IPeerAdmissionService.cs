namespace Morgana.AI.Interfaces;

/// <summary>
/// How many conversations one admitted partner may open on this installation within a sliding hour.
/// </summary>
/// <remarks>
/// The per-conversation budget says what one exchange may cost. Behind the A2A door that is only
/// half a bound: the caller names the conversation, so a partner rotating names would draw a
/// fresh budget every time. What is missing is therefore not a second measure of spend but a bound
/// on how many exchanges may be started at all, from which the ceiling on spend follows as
/// admissions times the budget each one carries.
/// <para>The counted event is an exchange this installation has never seen. A partner returning to
/// one it already opened is bounded by that conversation's own budget, so the two measures meet
/// without overlapping. This installation's own ring is exempt by construction — a colleague of
/// ours never opens a conversation, it joins the one the user is already having.</para>
/// </remarks>
public interface IPeerAdmissionService
{
    /// <summary>
    /// Weighs one partner's request to open a conversation it has not opened before, recording it
    /// when it is admitted.
    /// </summary>
    /// <remarks>
    /// Fails closed, unlike every other limiter here, because of where it stands: behind this door a
    /// request reaches an agent with none of the guard, classifier and channel rate limit a user's
    /// own path goes through, so this count is the whole of what bounds a partner. A count that
    /// cannot be read is therefore a refusal, never a conversation opened on the word of nobody.
    /// </remarks>
    /// <param name="issuer">Partner asking, as its token declared it.</param>
    /// <returns>Whether the conversation may be opened and, when it may not, what the partner is told.</returns>
    Task<Records.PeerAdmissionResult> TryAdmitNewConversationAsync(string issuer);
}