using Morgana.Models;

namespace Morgana.Interfaces;

public interface IStorageService
{
    Task SaveConversationAsync(ConversationEntry entry);
}