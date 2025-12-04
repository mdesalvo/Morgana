using Azure;
using Azure.Data.Tables;

namespace Morgana.Models;

public class ConversationEntry : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string UserId { get; set; } = default!;
    public string UserMessage { get; set; } = default!;
    public string BotResponse { get; set; } = default!;
    public string Category { get; set; } = default!;
    public string Intent { get; set; } = default!;
}