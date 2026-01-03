using Microsoft.AspNetCore.SignalR;
using Morgana.Hubs;
using Morgana.Interfaces;
using static Morgana.Records;

namespace Morgana.Services;

public class SignalRBridgeService : ISignalRBridgeService
{
    private readonly IHubContext<ConversationHub> hubContext;
    private readonly ILogger logger;

    public SignalRBridgeService(IHubContext<ConversationHub> hubContext, ILogger logger)
    {
        this.hubContext = hubContext;
        this.logger = logger;
    }

    public async Task SendMessageToConversationAsync(string conversationId, string text, string? errorReason = null)
    {
        await SendStructuredMessageAsync(conversationId, text, "assistant", null, errorReason, "Morgana");
    }

    public async Task SendStructuredMessageAsync(
        string conversationId,
        string text,
        string messageType,
        List<QuickReply>? quickReplies = null,
        string? errorReason = null,
        string? agentName = null)
    {
        logger.LogInformation($"Sending {messageType} message to conversation {conversationId} via SignalR");

        StructuredMessage message = new StructuredMessage(
            conversationId,
            text,
            DateTime.UtcNow,
            messageType,
            quickReplies,
            errorReason,
            agentName ?? "Morgana");

        await hubContext.Clients.Group(conversationId).SendAsync("ReceiveMessage", new
        {
            conversationId = message.ConversationId,
            text = message.Text,
            timestamp = message.Timestamp,
            messageType = message.MessageType,
            quickReplies = message.QuickReplies?.Select(qr => new
            {
                id = qr.Id,
                label = qr.Label,
                value = qr.Value
            }).ToList(),
            errorReason = message.ErrorReason,
            agentName = message.AgentName
        });
    }
}