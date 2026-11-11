namespace Morgana.AI.Interfaces;

/// <summary>
/// Writes Morgana's presentation, in her personality, for the start of a conversation and for a request that no agent claims.
/// Never throws: an implementation handles every error itself and at worst returns a fallback message with buttons built
/// from the intents it was given. The supervisor uses the result as it comes.
/// </summary>
public interface IPresenterService
{
    /// <summary>The welcome, its reintroduction without greeting and one button for each displayable intent.</summary>
    /// <param name="displayableIntents">The intents to offer, already without <see cref="Constants.Intents.Other"/> and without those lacking a <c>Label</c>.</param>
    /// <param name="conversationId">The conversation being presented, which an implementation may read to adapt to its channel.</param>
    Task<Records.PresentationResult> GenerateAsync(IReadOnlyList<Records.IntentDefinition> displayableIntents, string conversationId);
}