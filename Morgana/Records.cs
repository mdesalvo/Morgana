using System.Text.Json.Serialization;
using Akka.Actor;

namespace Morgana
{
    public static class Records
    {
        public record ClassificationResponse(
            [property: JsonPropertyName("category")] string Category,
            [property: JsonPropertyName("intent")] string Intent,
            [property: JsonPropertyName("confidence")] double Confidence);

        public record ClassificationResult(
            string Category,
            string Intent,
            Dictionary<string, string> Metadata);

        public record ConversationCreated(
            string ConversationId);

        public record ConversationResponse(
            string Response,
            string? Classification,
            Dictionary<string, string>? Metadata);

        public record CreateConversation(
            string ConversationId);

        public record ExecuteRequest(
            string ConversationId,
            string? Content,
            ClassificationResult? Classification);

        public record ExecuteResponse(
            string Response,
            bool IsCompleted = true);

        public record GuardCheckRequest(
            string ConversationId,
            string Message);

        public record GuardCheckResponse(
            [property: JsonPropertyName("compliant")] bool Compliant,
            [property: JsonPropertyName("violation")] string? Violation);

        public record InternalExecuteResponse(
            string Response,
            bool IsCompleted,
            IActorRef ExecutorRef);

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