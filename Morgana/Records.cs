using Akka.Actor;
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
            DateTime Timestamp);

        //Supervisor

        public record InitiateNewRequest(
            UserMessage Message,
            IActorRef OriginalSender);
        public record ContinueActiveSession(
            UserMessage Message,
            IActorRef OriginalSender);
        public record GuardCheckPassed(
            UserMessage Message,
            IActorRef OriginalSender);
        public record ClassificationReady(
            UserMessage Message,
            AI.Records.ClassificationResult Classification,
            IActorRef OriginalSender);
        public record AgentResponseReceived(
            AI.Records.InternalAgentResponse Response,
            AI.Records.ClassificationResult Classification,
            IActorRef OriginalSender);
    }
}