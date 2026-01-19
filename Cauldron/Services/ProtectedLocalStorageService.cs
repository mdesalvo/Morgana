using Cauldron.Interfaces;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Cauldron.Services;

/// <summary>
/// Implementation of conversation storage using ASP.NET Core ProtectedLocalStorage.
/// Provides encrypted, persistent storage of conversation IDs across browser sessions.
/// </summary>
public class ProtectedLocalStorageService : IConversationStorageService
{
    private readonly ProtectedLocalStorage protectedLocalStore;
    private readonly ILogger logger;
    private const string StorageKey = "morgana_conversation";

    public ProtectedLocalStorageService(
        ProtectedLocalStorage protectedLocalStore,
        ILogger logger)
    {
        this.protectedLocalStore = protectedLocalStore;
        this.logger = logger;
    }

    public async Task<string?> GetConversationIdAsync()
    {
        try
        {
            ProtectedBrowserStorageResult<string> result = 
                await protectedLocalStore.GetAsync<string>(StorageKey);
            
            if (result.Success)
            {
                logger.LogInformation($"Retrieved conversation ID from protected storage");
                return result.Value;
            }
            
            logger.LogInformation("No conversation ID found in protected storage");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retrieve conversation ID, clearing corrupted data");
            await ClearConversationIdAsync();
            return null;
        }
    }

    public async Task SaveConversationIdAsync(string conversationId)
    {
        try
        {
            await protectedLocalStore.SetAsync(StorageKey, conversationId);
            logger.LogInformation($"Saved conversation ID to protected storage: {conversationId}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save conversation ID to protected storage");
            throw;
        }
    }

    public async Task ClearConversationIdAsync()
    {
        try
        {
            await protectedLocalStore.DeleteAsync(StorageKey);
            logger.LogInformation("Cleared conversation ID from protected storage");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear conversation ID (may already be empty)");
        }
    }
}