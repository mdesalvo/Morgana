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
            string ConversationId,
            string UserId);

        public record ConversationResponse(
            string Response,
            string? Classification,
            Dictionary<string, string>? Metadata);

        public record CreateConversation(
            string ConversationId,
            string UserId);

        public record ExecuteRequest(
            string UserId,
            string SessionId,
            string? Content,
            ClassificationResult? Classification);

        public record ExecuteResponse(
            string Response,
            bool IsCompleted = true);

        public record GuardCheckRequest(
            string UserId,
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
            string UserId,
            string Text,
            Dictionary<string, object>? Metadata = null
        );

        public record StartConversationRequest(
            string ConversationId,
            string UserId,
            string? InitialContext = null);

        public record TerminateConversation(
            string ConversationId,
            string UserId);

        public record UserMessage(
            string ConversationId,
            string UserId,
            string Text,
            DateTime Timestamp
        );
    }
}