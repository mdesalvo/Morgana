using System.Text.Json.Serialization;

namespace Morgana
{
    public static class Records
    {
        public record BotResponse(
            string ConversationId,
            string Text,
            string? ErrorReason);

        public record ClassificationResponse(
            [property: JsonPropertyName("category")] string Category,
            [property: JsonPropertyName("intent")] string Intent,
            [property: JsonPropertyName("confidence")] double Confidence);

        public record ClassificationResult(
            string Category,
            string Intent,
            Dictionary<string, string> Metadata);

        public record ClearContextRequest;

        public record ClearContextResponse(bool Success);

        public record ConversationCreated(
            string ConversationId,
            string UserId);

        public record ConversationResponse(
            string Response,
            string Classification,
            Dictionary<string, string> Metadata);

        public record ConversationState(
            string ActiveAgentKey,
            string PendingToolName,
            Dictionary<string, object> CollectedParams
        );

        public record ConversationTimeout();

        public record CreateConversation(
            string ConversationId,
            string UserId);

        public record ExecuteRequest(
            string UserId,
            string SessionId,
            string Content,
            ClassificationResult Classification);

        public record ExecuteResponse(string Response);

        public record GuardCheckRequest(
            string UserId,
            string Message);

        public record GuardCheckResponse(
            [property: JsonPropertyName("compliant")] bool Compliant,
            [property: JsonPropertyName("violation")] string? Violation);

        public record QueryContextRequest;
        public record QueryContextResponse(ConversationState? State);

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

        public record UpdateContextRequest(ConversationState NewState);

        public record UpdateContextResponse(bool Success);

        public record UserMessage(
            string ConversationId,
            string UserId,
            string Text,
            DateTime Timestamp
        );

        public record UserMessageRequest(
            string UserId,
            string SessionId,
            string Message);
    }
}