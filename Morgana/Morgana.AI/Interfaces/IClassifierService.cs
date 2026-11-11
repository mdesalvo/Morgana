namespace Morgana.AI.Interfaces;

/// <summary>Classifies a user's message against the domain's intents, on behalf of the classifier actor.</summary>
// A failure never throws: the result names Constants.Intents.Other at confidence 0 and is marked as a classification error,
// so the turn degrades to Morgana's own answer instead of stopping.
public interface IClassifierService
{
    /// <summary>The intents that <paramref name="message"/> may express, ranked, with the top one named.</summary>
    /// <param name="conversationId">The conversation the message belongs to, for correlation and logs only: each message is judged on its own text.</param>
    /// <param name="message">The user's message.</param>
    /// <param name="cancellationToken">Stops the classification once whoever asked for it stops waiting, which is the one case that throws.</param>
    /// <returns>The result whose metadata carries at least <c>confidence</c>, plus <c>ambiguousIntents</c> when two candidates collide.</returns>
    Task<Records.ClassificationResult> ClassifyAsync(string conversationId, string message, CancellationToken cancellationToken = default);
}