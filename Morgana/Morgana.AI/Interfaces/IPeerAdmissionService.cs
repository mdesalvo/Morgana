namespace Morgana.AI.Interfaces;

/// <summary>Bounds how many conversations one partner may open on this installation within a sliding hour.</summary>
// The per-conversation budget says what one exchange may cost, but behind the A2A door the caller names the conversation:
// a partner rotating names would draw a fresh budget every time. What is bounded here is therefore how many exchanges may
// start at all, so the ceiling on spend is admissions times the budget each one carries. A partner returning to a
// conversation it already opened is bounded by that conversation's budget, so the two measures never overlap. This
// installation's own ring is exempt, because a colleague joins the conversation the user is already having.
public interface IPeerAdmissionService
{
    /// <summary>
    /// Weighs one partner's request to open a conversation it never opened before and records it when admitted.
    /// Fails closed, unlike every other limiter: behind this door a request reaches an agent with none of the guard,
    /// classifier and channel rate limit that a user's path goes through, so this count is all that bounds a partner.
    /// A count that cannot be read is a refusal, never a conversation opened on the word of nobody.
    /// </summary>
    /// <param name="issuer">The partner asking, as its token declared it.</param>
    /// <returns>Whether the conversation may be opened and, when it may not, what the partner is told.</returns>
    Task<Records.PeerAdmissionResult> TryAdmitNewConversationAsync(string issuer);
}