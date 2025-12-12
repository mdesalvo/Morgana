using System.Text.Json.Serialization;

namespace Morgana
{
    public static class Records
    {
        public record ConversationCreated(
            string ConversationId);

        public record ConversationResponse(
            string Response,
            string? Classification,
            Dictionary<string, string>? Metadata);

        public record CreateConversation(
            string ConversationId);

        public record GuardCheckRequest(
            string ConversationId,
            string Message);

        public record GuardCheckResponse(
            [property: JsonPropertyName("compliant")] bool Compliant,
            [property: JsonPropertyName("violation")] string? Violation);

        public record SendMessageRequest(
            string ConversationId,
            string Text,
            Dictionary<string, object>? Metadata = null
        );

        public record StartConversationRequest(
            string ConversationId,
            string? InitialContext = null);

        public record TerminateConversation(
            string ConversationId);

        public record UserMessage(
            string ConversationId,
            string Text,
            DateTime Timestamp
        );
    }
}