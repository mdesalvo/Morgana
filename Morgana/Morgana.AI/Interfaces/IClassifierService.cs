namespace Morgana.AI.Interfaces;

/// <summary>
/// Decides which intent a user message expresses.
/// Fail-safe contract: on failure an implementation returns a result with intent <see cref="Constants.Intents.Other"/>
/// and confidence 0.0 (silent degradation) and throws only on non-transient configuration failures at startup.
/// </summary>
public interface IClassifierService
{
    /// <summary>
    /// Classifies the given user message and returns the detected intent with metadata.
    /// </summary>
    /// <param name="conversationId">
    /// Unique identifier of the ongoing conversation. Passed for correlation and logging purposes
    /// only — classification itself is stateless per call, with no per-conversation memory kept
    /// between messages (each message is judged purely on its own text).
    /// </param>
    /// <param name="message">User message text to classify.</param>
    /// <returns>
    /// A <see cref="Records.ClassificationResult"/> containing the top-ranked intent name and a
    /// metadata dictionary (at minimum a <c>"confidence"</c> key). On failure, implementations
    /// must return a fallback result with intent <see cref="Constants.Intents.Other"/> rather than throwing — see the
    /// fail-safe contract above. Callers that need to detect a genuine collision between two or
    /// more close-scoring candidates read the metadata's <c>"ambiguousIntents"</c> key, present
    /// only when the implementation actually flags one.
    /// </returns>
    Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message);
}