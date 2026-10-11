using Cauldron.Interfaces;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Cauldron.Services;

/// <summary>
/// Persists the conversation id in the browser's localStorage through ProtectedLocalStorage,
/// which encrypts it with the server's data-protection keys. That is what lets a returning
/// visitor resume and what makes the value useless if lifted from the browser.
/// </summary>
public class ProtectedLocalStorageService : IConversationStorageService
{
    /// <summary>Encrypting wrapper over the browser's localStorage.</summary>
    private readonly ProtectedLocalStorage protectedLocalStore;

    /// <summary>Receives the storage outcomes that an operator may need to read.</summary>
    private readonly ILogger logger;

    /// <summary>Entry under which the conversation id is kept.</summary>
    private const string StorageKey = "morgana_conversation";

    /// <summary>
    /// Creates the storage service of one chat session.
    /// </summary>
    public ProtectedLocalStorageService(
        ProtectedLocalStorage protectedLocalStore,
        ILogger logger)
    {
        this.protectedLocalStore = protectedLocalStore;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetConversationIdAsync()
    {
        try
        {
            // Decrypted with the server's data-protection keys
            ProtectedBrowserStorageResult<string> storageResult =
                await protectedLocalStore.GetAsync<string>(StorageKey);

            // Success is false for a plain absence too, not just a decryption failure
            if (storageResult.Success)
            {
                logger.LogInformation("Retrieved conversation ID from protected storage");

                // The conversation this browser left behind
                return storageResult.Value;
            }

            logger.LogInformation("No conversation ID found in protected storage");

            // A first-time visitor: the caller starts a new conversation
            return null;
        }
        catch (Exception ex)
        {
            // Typically the data-protection keys rotated, leaving an entry that can no longer be
            // read. Clearing it turns a permanently broken load into one fresh conversation.
            logger.LogWarning(ex, "Failed to retrieve conversation ID, clearing corrupted data");
            await ClearConversationIdAsync();

            // Treated as a first-time visitor
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveConversationIdAsync(string conversationId)
    {
        try
        {
            // Encrypted before it reaches the browser
            await protectedLocalStore.SetAsync(StorageKey, conversationId);
            logger.LogInformation("Saved conversation ID to protected storage: {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            // Rethrown, unlike the other two: without a saved id the conversation cannot be
            // resumed later and silently continuing would hide that from the user.
            logger.LogError(ex, "Failed to save conversation ID to protected storage");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ClearConversationIdAsync()
    {
        try
        {
            // The next load finds no id and starts fresh
            await protectedLocalStore.DeleteAsync(StorageKey);
            logger.LogInformation("Cleared conversation ID from protected storage");
        }
        catch (Exception ex)
        {
            // Clearing is best effort: an entry that cannot be deleted is as good as one that is absent
            logger.LogWarning(ex, "Failed to clear conversation ID (may already be empty)");
        }
    }
}