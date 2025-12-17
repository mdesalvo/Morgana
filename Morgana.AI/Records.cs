using System.Text.Json;
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

        public record PromptCollection(
            Prompt[] Prompts);

        public record Prompt(
            string ID,
            string Type,
            string SubType,
            string Content,
            string Instructions,
            string Language,
            string Version,
            List<Dictionary<string, object>> AdditionalProperties)
        {
            public T GetAdditionalProperty<T>(string additionalPropertyName)
            {
                foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
                {
                    if (additionalProperties.TryGetValue(additionalPropertyName, out object value))
                    {
                        JsonElement element = (JsonElement)value;
                        return element.Deserialize<T>();
                    }
                }
                throw new KeyNotFoundException($"AdditionalProperty with key '{additionalPropertyName}' was not found in the prompt with id='{ID}'");
            }
        }

        public record ToolDefinition(
            string Name,
            string Description,
            IReadOnlyList<ToolParameter> Parameters);

        public record ToolParameter(
            string Name,
            string Description,
            bool Required);

        public record IntentCollection
        {
            public List<Dictionary<string, string>> Intents { get; set; }

            public IntentCollection(List<Dictionary<string, string>> intents)
            {
                Intents = intents;
            }

            public Dictionary<string, string> AsDictionary()
            {
                Dictionary<string, string> result = [];

                foreach (Dictionary<string, string> intentsDictionary in Intents)
                {
                    foreach (KeyValuePair<string, string> kvp in intentsDictionary)
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                }

                return result;
            }
        }
    }
}