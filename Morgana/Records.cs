using Akka.Actor;
using System.Text.Json.Serialization;

namespace Morgana;

public static class Records
{
    public record ConversationCreated(
        string ConversationId);

    public record ConversationResponse(
        string Response,
        string? Classification,
        Dictionary<string, string>? Metadata,
        string? AgentName = null);

    public record CreateConversation(
        string ConversationId);

    public record GuardCheckRequest(
        string ConversationId,
        string Message);

    public record GuardCheckResponse(
        [property: JsonPropertyName("compliant")] bool Compliant,
        [property: JsonPropertyName("violation")] string? Violation);

    public record SupervisorResponseContext(
        ConversationResponse Response);

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

    // Quick reply system

    public record QuickReply(
        string Id,
        string Label,
        string Value);

    public record StructuredMessage(
        string ConversationId,
        string Text,
        DateTime Timestamp,
        string MessageType,
        List<QuickReply>? QuickReplies = null,
        string? ErrorReason = null,
        string? AgentName = null);

    // Presentation flow messages

    public record GeneratePresentationMessage();

    public record PresentationContext(
        string Message,
        List<AI.Records.IntentDefinition> Intents)
    {
        // LLM-generated quick replies (takes precedence over Intents)
        public List<AI.Records.QuickReplyDefinition>? LlmQuickReplies { get; init; }
    };

    // Supervisor

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
        AI.Records.ActiveAgentResponse Response,
        AI.Records.ClassificationResult Classification,
        IActorRef OriginalSender);

    // Context wrappers for Become/PipeTo pattern (ConversationSupervisorActor)

    public record ProcessingContext(
        UserMessage OriginalMessage,
        IActorRef OriginalSender,
        AI.Records.ClassificationResult? Classification = null);

    public record GuardCheckContext(
        GuardCheckResponse Response,
        ProcessingContext Context);

    public record ClassificationContext(
        AI.Records.ClassificationResult Classification,
        ProcessingContext Context);

    public record AgentContext(
        object Response,
        ProcessingContext Context);

    public record FollowUpContext(
        AI.Records.AgentResponse Response,
        IActorRef OriginalSender);

    // Context wrappers for Become/PipeTo pattern (RouterActor)

    public record AgentResponseContext(
        AI.Records.AgentResponse Response,
        IActorRef AgentRef,
        IActorRef OriginalSender);

    // Context wrappers for Become/PipeTo pattern (GuardActor)

    public record LLMCheckContext(
        GuardCheckResponse Response,
        IActorRef OriginalSender);
}