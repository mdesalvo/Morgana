using Azure.Data.Tables;
using Morgana.Interfaces;
using Morgana.Models;

namespace Morgana.Services;

public class AzureStorageService : IStorageService
{
    private readonly IConfiguration configuration;
    private readonly ILogger<AzureStorageService> logger;
    private readonly TableServiceClient? tableServiceClient;

    public AzureStorageService(
        IConfiguration configuration,
        ILogger<AzureStorageService> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        tableServiceClient = new TableServiceClient(configuration["Azure:StorageConnectionString"]);
    }

    private async Task<TableClient> GetTableClientAsync(string tableName)
    {
        TableClient tableClient = tableServiceClient!.GetTableClient(tableName);
        await tableClient.CreateIfNotExistsAsync();
        return tableClient;
    }

    public async Task SaveConversationAsync(ConversationEntry entry)
    {
        try
        {
            TableClient tableClient = await GetTableClientAsync("MorganaConversations");
            await tableClient.AddEntityAsync(entry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving conversation to Azure Table Storage");
            throw;
        }
    }
}