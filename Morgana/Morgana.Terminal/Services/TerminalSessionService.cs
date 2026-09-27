using Morgana.Contracts;

namespace Morgana.Terminal.Services;

/// <summary>
/// Keeps track of the conversation on screen and keeps the webhook listening to it. Opening a conversation
/// is its one mutation, shared by the lifecycle at startup and by any command that replaces the conversation.
/// </summary>
public sealed class TerminalSessionService
{
    /// <summary>Opens conversations and carries user turns to Morgana.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Accepts only the deliveries of the conversation this service names.</summary>
    private readonly WebhookReceiverService webhookReceiverService;

    /// <summary>
    /// The conversation on screen with its seal, null until the first one opens; read by Kestrel's request threads
    /// too. One reference holds both, so no reader ever pairs one conversation's id with another's seal.
    /// </summary>
    private volatile OpenedConversation? openedConversation;

    /// <summary>Captures the REST client and the webhook dispatcher.</summary>
    public TerminalSessionService(MorganaClientService morganaClientService, WebhookReceiverService webhookReceiverService)
    {
        this.morganaClientService = morganaClientService;
        this.webhookReceiverService = webhookReceiverService;
    }

    /// <summary>When the conversation on screen was opened, in local time; null before the first one opens.</summary>
    public DateTimeOffset? OpenedAt { get; private set; }

    /// <summary>The conversation on screen.</summary>
    /// <exception cref="InvalidOperationException">Thrown before any conversation has been opened.</exception>
    public string ConversationId => CurrentConversation.Id;

    /// <summary>The seal of the conversation on screen, handed over by Morgana at start and held only in memory.</summary>
    /// <exception cref="InvalidOperationException">Thrown before any conversation has been opened.</exception>
    public string ConversationSeal => CurrentConversation.Seal;

    /// <summary>The conversation on screen with its seal, read together.</summary>
    private OpenedConversation CurrentConversation =>
        openedConversation ?? throw new InvalidOperationException("No conversation has been opened yet.");

    /// <summary>Tells whether a delivery addressed to <paramref name="conversationId"/> belongs to the conversation on screen.</summary>
    public bool IsConversationOnScreen(string conversationId) =>
        openedConversation is { } current && string.Equals(current.Id, conversationId, StringComparison.Ordinal);

    /// <summary>Sends a user turn on the conversation on screen. A 429 is not a failure: Morgana has already explained it over the webhook.</summary>
    public Task SendUserTurnAsync(string text)
    {
        // Read at send time: a turn typed after /new goes to the fresh conversation
        OpenedConversation current = CurrentConversation;
        return morganaClientService.SendMessageAsync(current.Id, current.Seal, text);
    }

    /// <summary>Opens a conversation in one attempt and makes it the one on screen; the previous one is not ended here.</summary>
    public async Task<string> OpenConversationAsync(CancellationToken cancellationToken)
    {
        // What the webhook falls back to if this attempt fails: the conversation still on screen, none at startup
        string? previousConversationId = openedConversation?.Id;

        // The webhook listens to the new id before the handshake goes out, since the presentation may land before
        // the reply. Each attempt proposes a fresh id, so what an abandoned attempt still delivers is refused
        string candidateConversationId = Guid.NewGuid().ToString("N");
        webhookReceiverService.ExpectedConversationId = candidateConversationId;
        try
        {
            // Morgana's id is the one that counts from here on, for the webhook and for every turn the user sends
            StartConversationResponse opened = await morganaClientService.StartConversationAsync(candidateConversationId, cancellationToken);
            webhookReceiverService.ExpectedConversationId = opened.ConversationId;
            openedConversation = new OpenedConversation(opened.ConversationId, opened.Seal);
            OpenedAt = DateTimeOffset.Now;
            return opened.ConversationId;
        }
        catch
        {
            // The conversation still on screen must go on hearing its replies, which Morgana redelivers meanwhile
            webhookReceiverService.ExpectedConversationId = previousConversationId;
            throw;
        }
    }

    /// <summary>A conversation Morgana opened and the seal every call on it carries.</summary>
    private sealed record OpenedConversation(string Id, string Seal);
}
