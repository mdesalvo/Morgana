namespace Morgana.AI.Interfaces;

/// <summary>
/// Produces the welcome message and the intent buttons shown to the user when a conversation starts.
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
    /// Filtered list of intents to present to the user (already excludes <see cref="Constants.Intents.Other"/>
    /// and intents without a <c>Label</c>). Implementations use these to build quick reply buttons.
    /// </param>
    /// <param name="conversationId">
    /// Identifier of the conversation. An implementation may use it to tailor the presentation to the conversation's channel.
    /// </param>
    /// <returns>
    /// A <see cref="Records.PresentationResult"/> containing the welcome message and the
    /// quick reply buttons to render in the UI. Never null; never throws.
    /// </returns>
    Task<Records.PresentationResult> GenerateAsync(IReadOnlyList<Records.IntentDefinition> displayableIntents, string conversationId);
}