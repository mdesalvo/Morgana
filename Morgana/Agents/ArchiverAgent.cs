using Akka.Actor;
using Morgana.Messages;
using Morgana.Interfaces;
using Morgana.Models;

namespace Morgana.Agents;

public class ArchiverAgent : MorganaAgent
{
    private readonly IStorageService storageService;
    private readonly ILogger<ArchiverAgent> logger;

    public ArchiverAgent(string conversationId, string userId, IStorageService storageService, ILogger<ArchiverAgent> logger) : base(conversationId, userId)
    {
        this.storageService = storageService;
        this.logger = logger;

        ReceiveAsync<ArchiveRequest>(ArchiveConversationAsync);
    }

    private async Task ArchiveConversationAsync(ArchiveRequest req)
    {
        IActorRef originalSender = Sender;

        ConversationEntry entry = new ConversationEntry
        {
            PartitionKey = req.SessionId,
            RowKey = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            UserId = req.UserId,
            UserMessage = req.UserMessage,
            BotResponse = req.BotResponse,
            Category = req.Classification.Category,
            Intent = req.Classification.Intent
        };

        await storageService.SaveConversationAsync(entry);
        logger.LogInformation($"Archived conversation for session {req.SessionId}");
    }
}