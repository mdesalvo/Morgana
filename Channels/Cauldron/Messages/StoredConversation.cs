namespace Cauldron.Messages;

/// <summary>
/// The conversation a returning visitor resumes, as kept in the browser: its id and the seal Morgana handed
/// over at start, without which the id opens nothing. Encrypted at rest by <c>ProtectedLocalStorage</c>.
/// </summary>
public sealed record StoredConversation(string ConversationId, string Seal);
