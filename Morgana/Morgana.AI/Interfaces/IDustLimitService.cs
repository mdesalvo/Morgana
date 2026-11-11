namespace Morgana.AI.Interfaces;

/// <summary>
/// Owns every question about a conversation's lifetime dust budget, from charging a call to the gauge that a channel shows.
/// The rate limiter bounds how often a conversation calls and this budget bounds what it consumes: exhaustion is terminal.
/// Every member fails open, because a fault of the ledger never blocks the user.
/// </summary>
public interface IDustLimitService
{
    /// <summary>Charges <paramref name="dust"/> to the conversation's budget and records the charge under <paramref name="llmRole"/>.</summary>
    /// <param name="conversationId">The conversation whose budget is charged.</param>
    /// <param name="dust">The dust to charge, computed from the call's tokens and its tier's price; nothing is charged when it is zero or less.</param>
    /// <param name="llmRole">Who consumed it, <c>Morgana</c> for the framework actors and <c>Morgana (intent)</c> for an agent; read by the ledger only.</param>
    Task ChargeAsync(string conversationId, double dust, string llmRole);

    /// <summary>Tells whether the conversation has spent its budget, so its next turn is blocked; false while unmetered or unread.</summary>
    Task<bool> IsOverBudgetAsync(string conversationId);

    /// <summary>
    /// The dust that the conversation has consumed so far; 0 while unmetered or unread.
    /// A caller subtracting two readings under-reports on a fault rather than inventing a cost.
    /// </summary>
    Task<double> GetConsumedAsync(string conversationId);

    /// <summary>What the work since <paramref name="baseline"/>, an earlier reading of <see cref="GetConsumedAsync"/>, cost; never negative.</summary>
    Task<double> GetConsumedSinceAsync(string conversationId, double baseline);

    /// <summary>
    /// The fraction of the budget still available, from 1 down to 0, in whole-percent steps; null while unmetered.
    /// Floored so that 0 is exactly the state IsOverBudgetAsync blocks on. Null tells a channel to show no gauge at all.
    /// </summary>
    Task<double?> GetRemainingLevelAsync(string conversationId);

    /// <summary>
    /// Which of the 70% and 90% warnings the conversation has just crossed, each reported once.
    /// Checks and marks in one step, so a threshold never triggers twice; neither is reported on a fault or while unmetered.
    /// </summary>
    Task<(bool Send70, bool Send90)> CheckAndMarkWarningsAsync(string conversationId);
}