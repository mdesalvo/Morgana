using System.Text.Json.Serialization;
using Akka.Actor;

namespace Morgana.AI
{
    public static class Records
    {
        public record AgentRequest(
            string ConversationId,
            string? Content,
            ClassificationResult? Classification);

        public record AgentResponse(
            string Response,
            bool IsCompleted = true);

        public record ClassificationResponse(
            [property: JsonPropertyName("intent")] string Intent,
            [property: JsonPropertyName("confidence")] double Confidence);

        public record ClassificationResult(
            string Intent,
            Dictionary<string, string> Metadata);

        public record InternalAgentResponse(
           string Response,
           bool IsCompleted,
           IActorRef AgentRef);
    }
}