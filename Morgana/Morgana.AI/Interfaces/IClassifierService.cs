namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for intent classification. Decouples logic from ClassifierActor which delegates entirely to this.
/// Default implementation: LLMClassifierService using agents.json intents + Classifier prompt from morgana.json.
/// Fail-safe contract: on failure return ClassificationResult with intent="other" and confidence=0.0 (silent degradation);
/// only throw on non-transient configuration failures at startup (fail-fast). Swappable via DI.
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
    /// must return a fallback result with intent <c>"other"</c> rather than throwing — see the
    /// fail-safe contract above. Callers that need to detect a genuine collision between two or
    /// more close-scoring candidates read the metadata's <c>"ambiguousIntents"</c> key, present
    /// only when the implementation actually flags one.
    /// </returns>
    Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message);
}