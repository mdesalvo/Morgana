namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for generating the initial presentation message shown to the user when a new conversation starts.
/// Decouples presentation generation from the actor infrastructure. ConversationSupervisorActor delegates entirely
/// to this service and is agnostic of the underlying generation strategy.
/// Default implementation: LLMPresenterService provides LLM-driven presentation generation
/// with an internal fallback to config-based quick replies.
/// Reliability contract: Implementations must never throw. They are expected to handle all errors internally
/// and always return a valid <see cref="Records.PresentationResult"/> — at minimum a sensible
/// fallback message with quick replies derived directly from the provided intent definitions.
/// The actor trusts the result unconditionally.
/// </summary>
public interface IPresenterService
{
    /// <summary>
    /// Generates the presentation message and quick reply buttons for the start of a conversation.
    /// </summary>
    /// <param name="displayableIntents">
    /// Filtered list of intents to present to the user (already excludes <c>"other"</c>
    /// and intents without a <c>Label</c>). Implementations use these to build quick reply buttons.
    /// </param>
    /// <param name="conversationId">
    /// Identifier of the conversation. The implementation is free to use it for channel-aware
    /// behaviour (e.g. resolving channel metadata to drive a per-channel cache); callers stay
    /// agnostic of any such mechanism.
    /// </param>
    /// <returns>
    /// A <see cref="Records.PresentationResult"/> containing the welcome message and the
    /// quick reply buttons to render in the UI. Never null; never throws.
    /// </returns>
    Task<Records.PresentationResult> GenerateAsync(IReadOnlyList<Records.IntentDefinition> displayableIntents, string conversationId);
}