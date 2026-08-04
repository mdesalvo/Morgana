using Cauldron.Interfaces;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Cauldron.Services;

/// <summary>
/// Persists the conversation id in the browser's localStorage through ProtectedLocalStorage,
/// which encrypts it with the server's data-protection keys. That is what lets a returning
/// visitor resume, and what makes the value useless if lifted from the browser.
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

            // Success is false for a plain absence too, not just a decryption failure
            if (result.Success)
            {
                logger.LogInformation("Retrieved conversation ID from protected storage");
                return result.Value;
            }

            logger.LogInformation("No conversation ID found in protected storage");
            return null;
        }
        catch (Exception ex)
        {
            // Typically the data-protection keys rotated, leaving an entry that can no longer be
            // read. Clearing it turns a permanently broken load into one fresh conversation.
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
            logger.LogInformation("Saved conversation ID to protected storage: {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            // Rethrown, unlike the other two: without a saved id the conversation cannot be
            // resumed later, and silently continuing would hide that from the user.
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