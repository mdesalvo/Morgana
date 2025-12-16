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
            [property: JsonPropertyName("id")] string ID,
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("subtype")] string SubType,
            [property: JsonPropertyName("content")] string Content,
            [property: JsonPropertyName("instructions")] string Instructions,
            [property: JsonPropertyName("language")] string Language,
            [property: JsonPropertyName("version")] string Version,
            [property: JsonPropertyName("additionalProperties")] List<Dictionary<string, object>> AdditionalProperties)
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
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("description")] string Description,
            [property: JsonPropertyName("parameters")] IReadOnlyList<ToolParameter> Parameters);

        public record ToolParameter(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("description")] string Description,
            [property: JsonPropertyName("required")] bool Required);

        public record IntentCollection
        {
            [JsonPropertyName("intents")] public List<Dictionary<string, string>> Intents { get; set; }

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