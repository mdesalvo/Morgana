namespace Rune.Messages.Contracts;

/// <summary>
/// <para>
/// Body payload Rune sends to <c>POST /api/morgana/conversation/{conversationId}/message</c>.
/// Carries both the conversation id (which the controller reads from the body, not from the
/// route token) and the user's text.
/// </para>
/// <para>
/// CONTRACT VERSION: 1.0<br/>
/// SYNC WITH: Morgana.AI (Records.SendMessageRequest)
/// </para>
/// <para>
/// ⚠️ IMPORTANT: This DTO is duplicated in Morgana (Morgana.AI.Records.SendMessageRequest).
/// When making changes, update both versions in lockstep — there is no shared contracts project.
/// </para>
/// </summary>
public sealed class SendMessageRequest
{
    /// <summary>Target conversation id. Required — read from the body by MorganaController.SendMessage.</summary>
    public required string ConversationId { get; set; }

    /// <summary>User-supplied message text. Required.</summary>
    public required string Text { get; set; }
}
