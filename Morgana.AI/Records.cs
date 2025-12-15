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

        public record Prompt(
            [property: JsonPropertyName("id")] string ID,
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("subtype")] string SubType,
            [property: JsonPropertyName("content")] string Content,
            [property: JsonPropertyName("instructions")] string Instructions,
            [property: JsonPropertyName("language")] string Language,
            [property: JsonPropertyName("version")] string Version,
            [property: JsonPropertyName("additionalProperties")] Dictionary<string, object> AdditionalProperties);
    }
}