namespace Rune.Messages;

/// <summary>
/// Response model from <c>POST /api/morgana/conversation/start</c>. Mirrors the anonymous
/// object returned by <c>MorganaController.StartConversation</c>, echoing back the
/// conversation id Rune minted on the request plus an informational status message.
/// </summary>
/// <remarks>
/// <para>Deliberately lives under <c>Messages/</c> (not <c>Messages/Contracts/</c>) because the
/// server side of this shape is an anonymous response, not a declared record in
/// <c>Morgana.AI.Records</c> — the same convention Cauldron follows for its
/// <c>ConversationStartResponse</c>. Renames in the controller must therefore be mirrored
/// here manually.</para>
/// </remarks>
public sealed class StartConversationResponse
{
    /// <summary>Conversation id echoed back by Morgana (same value Rune sent on the request).</summary>
    public string ConversationId { get; set; } = string.Empty;
}
