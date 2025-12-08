using Akka.Actor;
using Morgana.Messages;
using Morgana.Interfaces;
using Morgana.Models;

namespace Morgana.Agents;

public class ArchiverAgent : ReceiveActor
{
    private readonly IStorageService _storageService;
    private readonly ILogger<ArchiverAgent> logger;

    public ArchiverAgent(IStorageService storageService, ILogger<ArchiverAgent> logger)
    {
        _storageService = storageService;
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

        await _storageService.SaveConversationAsync(entry);
        logger.LogInformation($"Archived conversation for session {req.SessionId}");
    }
}