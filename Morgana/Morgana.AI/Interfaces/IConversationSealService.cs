namespace Morgana.AI.Interfaces;

/// <summary>
/// Owns every question about a conversation's seal: the secret Morgana hands the channel at start,
/// without which knowing a conversation id buys nothing.
/// </summary>
/// <remarks>
/// The seal defends Morgana's users from one another, never from whoever runs the server: it is shown
/// once, stored only as a hash and bound to the issuer that opened the conversation, so resuming it
/// from another channel is refused by construction.
/// </remarks>
public interface IConversationSealService
{
    /// <summary>
    /// Seals a conversation not yet on record under the issuer opening it and returns the seal in clear.
    /// Null when the conversation already exists: the one atomic gate keeping start from reopening an id.
    /// </summary>
    Task<string?> SealAsync(string conversationId, string issuer);

    /// <summary>
    /// True only when the conversation exists, was sealed under <paramref name="issuer"/> and
    /// <paramref name="presentedSeal"/> is its seal. A conversation sealed by no one is never admitted.
    /// </summary>
    Task<bool> VerifyAsync(string conversationId, string issuer, string? presentedSeal);
}