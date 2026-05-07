namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for generating the initial presentation message shown to the user
/// when a new conversation starts.
/// Implementations can range from LLM-based generation to static templates,
/// tenant-specific content, CMS-driven messages, or A/B test variants.
/// </summary>
/// <remarks>
/// <para><strong>Design Intent:</strong></para>
/// <para>Decouples presentation generation from the actor infrastructure.
/// <see cref="Actors.ConversationSupervisorActor"/> delegates entirely to this service
/// and is agnostic of the underlying generation strategy.</para>
///
/// <para><strong>Default Implementation:</strong></para>
/// <para><see cref="Services.LLMPresenterService"/> provides LLM-driven presentation generation
/// with an internal fallback to config-based quick replies, identical to the previous behaviour
/// embedded in the actor. Swap it in DI to adopt any alternative strategy without touching
/// the actor system.</para>
///
/// <para><strong>Reliability Contract:</strong></para>
/// <para>Implementations must never throw. They are expected to handle all errors internally
/// and always return a valid <see cref="Records.PresentationResult"/> — at minimum a sensible
/// fallback message with quick replies derived directly from the provided intent definitions.
/// The actor trusts the result unconditionally.</para>
/// </remarks>
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