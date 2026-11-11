namespace Morgana.Terminal.Services;

/// <summary>
/// Keeps track of the conversation on screen and keeps the webhook listening to it.
/// Opening a conversation is its one mutation, shared by the lifecycle at startup and by any command that replaces the conversation.
/// </summary>
public sealed class TerminalSessionService
{
    /// <summary>Opens conversations and carries user turns to Morgana.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Accepts only the deliveries of the conversation this service names.</summary>
    private readonly WebhookReceiverService webhookReceiverService;

    /// <summary>Backing store of <see cref="ConversationId"/>, null until the first conversation opens and read by Kestrel's request threads too.</summary>
    private volatile string? conversationId;

    /// <summary>Builds the session on the REST client and the webhook dispatcher.</summary>
    public TerminalSessionService(MorganaClientService morganaClientService, WebhookReceiverService webhookReceiverService)
    {
        // The collaborators the session opens conversations and routes deliveries through.
        this.morganaClientService = morganaClientService;
        this.webhookReceiverService = webhookReceiverService;
    }

    /// <summary>When the conversation on screen was opened, in local time; null before the first one opens.</summary>
    public DateTimeOffset? OpenedAt { get; private set; }

    /// <summary>The conversation on screen.</summary>
    /// <exception cref="InvalidOperationException">No conversation has been opened yet.</exception>
    public string ConversationId => conversationId ?? throw new InvalidOperationException("No conversation has been opened yet.");

    /// <summary>Tells whether a delivery addressed to <paramref name="conversationId"/> belongs to the conversation on screen.</summary>
    public bool IsConversationOnScreen(string conversationId) =>
        this.conversationId is { } currentConversationId && string.Equals(currentConversationId, conversationId, StringComparison.Ordinal);

    /// <summary>
    /// Sends a user turn on the conversation on screen.
    /// A 429 is not a failure: Morgana has already explained it over the webhook.
    /// </summary>
    public Task SendUserTurnAsync(string text) =>
        // Read at send time, so a turn typed after /new goes to the fresh conversation.
        morganaClientService.SendMessageAsync(ConversationId, text);

    /// <summary>Opens a conversation in one attempt and makes it the one on screen, leaving the previous one to its caller to end.</summary>
    public async Task<string> OpenConversationAsync(CancellationToken cancellationToken)
    {
        // What the webhook falls back to if this attempt fails: the conversation still on screen, none at startup.
        string? previousConversationId = conversationId;

        // The webhook listens to the new id before the handshake goes out, since the presentation may land before the reply.
        // Each attempt proposes a fresh id, so what an abandoned attempt still delivers is refused.
        string candidateConversationId = Guid.NewGuid().ToString("N");
        webhookReceiverService.ExpectedConversationId = candidateConversationId;

        try
        {
            // Morgana's id is the one that counts from here on, for the webhook and for every turn the user sends.
            string openedConversationId = await morganaClientService.StartConversationAsync(candidateConversationId, cancellationToken);
            webhookReceiverService.ExpectedConversationId = openedConversationId;
            conversationId = openedConversationId;
            OpenedAt = DateTimeOffset.Now;

            // The opened conversation is the one the screen shows from now on.
            return openedConversationId;
        }
        catch
        {
            // The conversation still on screen must go on hearing its replies, which Morgana redelivers meanwhile.
            webhookReceiverService.ExpectedConversationId = previousConversationId;
            throw;
        }
    }
}