namespace Morgana.AI.Interfaces;

/// <summary>
/// Enforces per-conversation lifetime token budget (orthogonal to rate limiter).
/// Rate limiter controls message frequency; dust limiter controls token consumption.
/// Fixed budget per conversation; exhaustion is terminal (next turn blocked). All methods
/// fail open — storage faults never block the user.
/// </summary>
public interface IDustLimitService
{
    /// <summary>
    /// Records dust consumed by a single LLM call and appends an audit-log row.
    /// </summary>
    /// <param name="conversationId">Conversation whose budget is charged.</param>
    /// <param name="dust">Dust units to add (computed by the caller from token counts and
    /// per-provider pricing). Calls with <paramref name="dust"/> &lt;= 0 are no-ops.</param>
    /// <param name="llmRole">Who burned it — <c>"Morgana"</c> for framework actors,
    /// <c>"Morgana (Intent)"</c> for domain agents. Diagnostic only, not used for enforcement.</param>
    Task ChargeAsync(string conversationId, double dust, string llmRole);

    /// <summary>
    /// True when the conversation has consumed its full budget and the next turn must be
    /// blocked. False (fail open) on storage errors or when dust limiting is disabled.
    /// </summary>
    Task<bool> IsOverBudgetAsync(string conversationId);

    /// <summary>
    /// Ratio of consumed dust to the configured budget (0.0 to &gt;1.0). 0.0 when the
    /// conversation has no usage yet, when dust limiting is disabled, or on error.
    /// </summary>
    Task<double> GetUsageRatioAsync(string conversationId);

    /// <summary>
    /// Atomically checks the 70% and 90% thresholds against their one-shot flags, marking
    /// any newly-crossed threshold so it never re-triggers. Returns which warnings the caller
    /// should emit. (false, false) on error or when disabled.
    /// </summary>
    Task<(bool Send70, bool Send90)> CheckAndMarkWarningsAsync(string conversationId);
}
