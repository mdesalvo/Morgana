using Azure;
using Azure.Data.Tables;

namespace Morgana.Models;

public class ConversationEventEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // conversationId
    public string RowKey { get; set; } = string.Empty; // eventId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EventData { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}