using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Morgana.AI.Extensions;

/// <summary>
/// Reads the properties that Morgana stamps on a chat message, whether the message is in memory or was read back from the record.
/// </summary>
internal static class ChatMessageExtensions
{
    extension(ChatMessage chatMessage)
    {
        /// <summary>
        /// Text of a stamped property, <c>null</c> when the property is absent or is not text.
        /// </summary>
        public string? ReadTextProperty(string propertyKey)
        {
            // Absent on a message that was never stamped with this property, which is the case of every user turn.
            if (chatMessage.AdditionalProperties?.TryGetValue(propertyKey, out object? propertyValue) != true)
                return null;

            // A session still in memory hands back what was stored while one read back from the record
            // hands back JSON. Anything else is treated as absent rather than trusted, so the caller
            // stays on its own path instead of acting on a value nobody wrote.
            return propertyValue switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
        }
    }
}