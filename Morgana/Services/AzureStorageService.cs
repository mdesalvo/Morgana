using Azure.Data.Tables;
using Morgana.Interfaces;
using Morgana.Models;

namespace Morgana.Services;

public class AzureStorageService : IStorageService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly ILogger<AzureStorageService> logger;
    private TableClient? _tableClient;

    public AzureStorageService(
        TableServiceClient tableServiceClient,
        ILogger<AzureStorageService> logger)
    {
        _tableServiceClient = tableServiceClient;
        this.logger = logger;
    }

    public async Task SaveConversationAsync(ConversationEntry entry)
    {
        try
        {
            _tableClient ??= _tableServiceClient.GetTableClient("MorganaConversations");
            await _tableClient.CreateIfNotExistsAsync();
            await _tableClient.AddEntityAsync(entry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving conversation to Azure Table Storage");
            throw;
        }
    }
}