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
    /// Unique identifier of the ongoing conversation.
    /// Passed for correlation and logging purposes.
    /// </param>
    /// <param name="message">User message text to classify.</param>
    /// <returns>
    /// A <see cref="Records.ClassificationResult"/> containing the detected intent name
    /// and a metadata dictionary (at minimum a <c>"confidence"</c> key).
    /// On failure, implementations must return a fallback result with intent <c>"other"</c>.
    /// </returns>
    Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message);
}